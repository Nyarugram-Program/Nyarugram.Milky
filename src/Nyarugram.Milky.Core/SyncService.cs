using System.Net.WebSockets;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Storage;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

/// <summary>Wakes the sync loop when the backend publishes a new cursor watermark.</summary>
public sealed class SyncSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1024);

    public void Pulse()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Coalesced wakeups are fine; the difference query is authoritative anyway.
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _semaphore.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public interface ICallUpdateSink
{
    Task ApplyAsync(SessionContext context, P.PhoneCall call, CancellationToken ct);
    Task ApplySignalingAsync(SessionContext context, P.PhoneCallSignalingData data, CancellationToken ct);
}

/// <summary>
/// Applies get_difference pages. A hint only wakes this loop; the delta is always read
/// from the backend so a missed wake-up can never lose an update.
///
/// Failure policy:
/// - transient failures (transport, 5xx, storage) abort the tick before the cursor commits,
///   so the same page is retried and no message is skipped;
/// - permanent per-message failures (unsupported content, deleted object, permission loss)
///   are recorded in message_skip and the page may advance;
/// - a message that cannot be represented never blocks the account forever.
/// </summary>
public sealed class SyncService : BackgroundService
{
    private readonly MilkyRuntime _runtime;
    private readonly BackendRpc _rpc;
    private readonly IdentityResolver _identities;
    private readonly MessageMapper _mapper;
    private readonly SyncStore _store;
    private readonly AccountStore _accounts;
    private readonly ICallUpdateSink _calls;
    private readonly SyncSignal _signal;
    private readonly RuntimeOptions _options;
    private readonly ILogger<SyncService> _logger;

    private DateTimeOffset _lastTooLongReset = DateTimeOffset.MinValue;

    public SyncService(
        MilkyRuntime runtime, BackendRpc rpc, IdentityResolver identities, MessageMapper mapper,
        SyncStore store, AccountStore accounts, ICallUpdateSink calls, SyncSignal signal,
        RuntimeOptions options, ILogger<SyncService> logger)
    {
        _runtime = runtime;
        _rpc = rpc;
        _identities = identities;
        _mapper = mapper;
        _store = store;
        _accounts = accounts;
        _calls = calls;
        _signal = signal;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.BackgroundSync)
        {
            _logger.LogWarning("background sync is disabled by configuration; only explicit syncs run");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var context = _runtime.Current;
            if (context is null)
            {
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunOnceAsync(context, stoppingToken).ConfigureAwait(false);
            }
            catch (MilkyActionException ex) when (ex.Code == MilkyErrorCode.NotLoggedIn)
            {
                _logger.LogWarning("sync stopped: session generation is no longer current");
            }
            catch (BackendException ex)
            {
                _logger.LogWarning("sync backend error {Code} retryable={Retryable}; page will be retried",
                    ex.Code, ex.Retryable);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (Npgsql.NpgsqlException ex)
            {
                _logger.LogError(ex, "sync storage error; cursor not advanced");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one malformed update kill the loop; log loudly and keep the cursor
                // where it is so the page is retried.
                _logger.LogError(ex, "sync tick failed unexpectedly; cursor not advanced");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }

            await _signal.WaitAsync(_options.SyncInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Processes up to a bounded number of pages so latency stays predictable.</summary>
    public async Task RunOnceAsync(SessionContext context, CancellationToken ct)
    {
        for (var page = 0; page < _options.MaxSyncPagesPerTick; page++)
        {
            var cursor = await _accounts.GetCursorAsync(context.Account.AccountId, ct).ConfigureAwait(false);
            var response = await _rpc.CallAsync(context, new P.RpcRequest
            {
                GetDifference = new P.GetDifferenceRequest { Pts = (int)Math.Min(int.MaxValue, cursor.Pts) },
            }, ct).ConfigureAwait(false);

            var difference = response.GetDifference
                ?? throw new MilkyActionException(MilkyErrorCode.Transient, "backend returned no difference");

            switch (difference.KindCase)
            {
                case P.Difference.KindOneofCase.TooLong:
                {
                    // A pts beyond the backend's replay window (for example after expired call
                    // signaling shortened the stream) must re-baseline immediately. Resetting to
                    // zero is always safe: the next page is replayed and stable event ids dedupe.
                    if (cursor.Pts > 0)
                    {
                        _lastTooLongReset = DateTimeOffset.UtcNow;
                        _logger.LogWarning(
                            "difference cursor {Pts} is beyond the replay window ({BackendPts}); re-baselining from zero",
                            cursor.Pts, difference.TooLong.Pts);
                        await _store.ResetCursorAsync(context.Account.AccountId, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (DateTimeOffset.UtcNow - _lastTooLongReset < TimeSpan.FromMinutes(1))
                    {
                        _logger.LogWarning("difference still too long at pts zero ({BackendPts}); backing off",
                            difference.TooLong.Pts);
                        return;
                    }
                    _lastTooLongReset = DateTimeOffset.UtcNow;
                    await _store.ResetCursorAsync(context.Account.AccountId, ct).ConfigureAwait(false);
                    return;
                }
                case P.Difference.KindOneofCase.Empty:
                {
                    await _store.CommitAsync(context.Account.AccountId, [], [], [],
                        cursor with { DateMs = difference.Empty.DateMs, Seq = difference.Empty.Seq, Initialized = true }, ct)
                        .ConfigureAwait(false);
                    return;
                }
                case P.Difference.KindOneofCase.Complete:
                case P.Difference.KindOneofCase.Slice:
                {
                    var pageData = difference.KindCase == P.Difference.KindOneofCase.Complete
                        ? difference.Complete
                        : difference.Slice;
                    var isSlice = difference.KindCase == P.Difference.KindOneofCase.Slice;
                    await ApplyPageAsync(context, pageData, ct).ConfigureAwait(false);
                    if (!isSlice) return;
                    break;
                }
                default:
                    throw new MilkyActionException(MilkyErrorCode.Transient,
                        $"unexpected difference kind {difference.KindCase}");
            }
        }
        _logger.LogDebug("sync page budget reached; continuing next tick");
        _signal.Pulse();
    }

    private async Task ApplyPageAsync(SessionContext context, P.DifferencePage page, CancellationToken ct)
    {
        await _identities.RememberSummariesAsync(context, page.Users, page.Groups, ct).ConfigureAwait(false);

        var rows = new List<MessageRow>(page.Messages.Count);
        var skips = new List<(long MessageId, string Reason)>();
        var events = new List<OutboxMessage>(page.Messages.Count);
        var received = new List<(long MessageId, long PeerId, long SenderId, bool Group)>();
        var selfId = context.Account.Uin;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var message in page.Messages)
        {
            IncomingMessage milky;
            try
            {
                milky = await _mapper.ToMilkyAsync(context, message, ct).ConfigureAwait(false);
            }
            catch (MilkyActionException ex) when (IsPermanent(ex))
            {
                skips.Add((message.MessageId, $"unsupported:{ex.Message}"));
                _logger.LogWarning("message {MessageId} skipped permanently: {Reason}", message.MessageId, ex.Message);
                continue;
            }
            // Transient mapping failures propagate: the page is retried with the cursor untouched.
            catch (BackendException ex) when (IsPermanent(ex))
            {
                skips.Add((message.MessageId, $"backend:{ex.Code}"));
                _logger.LogWarning("message {MessageId} skipped permanently: {Code}", message.MessageId, ex.Code);
                continue;
            }

            rows.Add(new MessageRow(
                message.MessageId,
                milky is GroupMessage ? PeerKind.Group : PeerKind.User,
                milky.PeerId,
                milky.SenderId,
                message.CreatedAtMs,
                message.Outgoing));

            if (message.Outgoing) continue;
            received.Add((message.MessageId, milky.PeerId, milky.SenderId, milky is GroupMessage));
            var evt = new MilkyEvent
            {
                Time = now,
                SelfId = selfId,
                EventType = "message_receive",
                Data = milky,
            };
            // The event id is derived from the durable message id, so a replayed difference
            // reuses the same identity and the outbox dedupes it.
            events.Add(new OutboxMessage($"msg:{message.MessageId}", evt.EventType,
                System.Text.Json.JsonSerializer.Serialize(evt, MilkyJson.Options)));
        }

        foreach (var update in page.Updates)
        {
            switch (update.KindCase)
            {
                case P.Update.KindOneofCase.PhoneCall when update.PhoneCall?.PhoneCall is { } call:
                    try
                    {
                        await _calls.ApplyAsync(context, call, ct).ConfigureAwait(false);
                    }
                    catch (MilkyActionException ex) when (ex.Code is MilkyErrorCode.Transient or MilkyErrorCode.NotLoggedIn)
                    {
                        // Transient: abort the page so the update is replayed with the cursor intact.
                        throw;
                    }
                    catch (BackendException ex) when (!IsPermanent(ex))
                    {
                        throw;
                    }
                    catch (MilkyActionException ex)
                    {
                        _logger.LogWarning("call update {CallId} skipped permanently: {Reason}", call.CallId, ex.Message);
                    }
                    catch (BackendException ex)
                    {
                        _logger.LogWarning("call update {CallId} skipped permanently: {Code}", call.CallId, ex.Code);
                    }
                    catch (DhValidationException ex)
                    {
                        // A bad peer value is permanent for this call only; the page continues.
                        _logger.LogError("call update {CallId} rejected: {Code}", call.CallId, ex.Code);
                    }
                    catch (Exception ex) when (ex is InvalidProtocolBufferException or FormatException)
                    {
                        _logger.LogError(ex, "call update {CallId} carried malformed data", call.CallId);
                    }
                    break;
                case P.Update.KindOneofCase.PhoneCallSignaling when update.PhoneCallSignaling is { } signaling:
                    try
                    {
                        await _calls.ApplySignalingAsync(context, signaling, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                        or ObjectDisposedException)
                    {
                        // The media session for an ending call is gone; a stale signal must not
                        // abort the page that also carries messages and call state updates.
                        _logger.LogDebug("stale call signal for {CallId} dropped: {Reason}",
                            signaling.CallId, ex.Message);
                    }
                    break;
                case P.Update.KindOneofCase.NewMessage:
                    // Already covered by page.Messages; ignoring avoids duplicate events.
                    break;
                default:
                    // Outside the first-phase Milky subset; intentionally not surfaced.
                    break;
            }
        }

        var state = page.State ?? new P.SyncState();
        var cursor = new SyncCursor(state.Pts, state.Qts, state.Seq, state.DateMs, true);
        await _store.CommitAsync(context.Account.AccountId, rows, skips, events, cursor, ct).ConfigureAwait(false);

        // Logged only after the cursor commit, so a transient failure does not report a
        // message that will be replayed. Message content is never written to logs.
        foreach (var item in received)
        {
            _logger.LogInformation(
                "message_receive id={MessageId} peer={PeerKind}:{PeerId} sender={SenderId}",
                item.MessageId, item.Group ? "group" : "user", item.PeerId, item.SenderId);
        }

        _logger.LogDebug("applied {Messages} messages, {Skipped} skipped, {Events} events, pts={Pts}",
            rows.Count, skips.Count, events.Count, state.Pts);
    }

    /// <summary>
    /// A permanent failure will not succeed on retry: the content is unsupported, the object
    /// is gone or the account lost access. Everything else is treated as transient so the
    /// cursor never advances past an event that could still be delivered.
    /// </summary>
    public static bool IsPermanent(MilkyActionException ex) => ex.Code is
        MilkyErrorCode.Unsupported or MilkyErrorCode.NotFound or MilkyErrorCode.BadRequest or MilkyErrorCode.Conflict;

    public static bool IsPermanent(BackendException ex) => ex.HttpStatus is 400 or 403 or 404 or 409 or 501;
}

/// <summary>
/// Maintains the botgram.event-hint.v1.protobuf connection. Reconnects with backoff and
/// reconnects immediately when the session generation changes.
/// </summary>
public sealed class EventHintListener : BackgroundService
{
    private readonly MilkyRuntime _runtime;
    private readonly BackendClient _client;
    private readonly SyncSignal _signal;
    private readonly ILogger<EventHintListener> _logger;

    public EventHintListener(
        MilkyRuntime runtime, BackendClient client, SyncSignal signal, ILogger<EventHintListener> logger)
    {
        _runtime = runtime;
        _client = client;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var context = _runtime.Current;
            if (context is null)
            {
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var socket = await _client.ConnectEventStreamAsync(context.BearerToken, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("event hint stream connected");
                delay = TimeSpan.FromSeconds(1);
                await PumpAsync(context, socket, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is BackendException or WebSocketException or InvalidProtocolBufferException)
            {
                if (!_runtime.IsCurrent(context))
                {
                    _logger.LogInformation("event stream dropped after session change");
                    continue;
                }
                _logger.LogWarning(ex, "event hint stream failed; retrying in {Delay}", delay);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task PumpAsync(SessionContext context, ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[512];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close) return;
            if (received.Count == 0) continue;
            if (!_runtime.IsCurrent(context))
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session changed", ct).ConfigureAwait(false);
                return;
            }
            try
            {
                var hint = P.EventHint.Parser.ParseFrom(buffer.AsSpan(0, received.Count));
                if (hint.ProtocolMajor != BackendClient.ProtocolMajor) continue;
                _signal.Pulse();
            }
            catch (InvalidProtocolBufferException ex)
            {
                _logger.LogWarning(ex, "malformed event hint ignored");
            }
        }
    }
}

/// <summary>
/// Moves durable outbox rows to connected consumers.
///
/// A row is marked delivered only when every connected consumer acknowledged it; with no
/// consumer the row stays pending and is eventually expired (never retired as delivered).
/// Consumers resume from their own persisted cursor, so one slow subscriber cannot make
/// another lose events.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly MilkyRuntime _runtime;
    private readonly OutboxStore _outbox;
    private readonly EventBus _bus;
    private readonly RuntimeOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue;

    public OutboxDispatcher(
        MilkyRuntime runtime, OutboxStore outbox, EventBus bus, RuntimeOptions options, ILogger<OutboxDispatcher> logger)
    {
        _runtime = runtime;
        _outbox = outbox;
        _bus = bus;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var context = _runtime.Current;
            if (context is null)
            {
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await DrainAsync(context, stoppingToken).ConfigureAwait(false);
                await MaintainAsync(context, stoppingToken).ConfigureAwait(false);
            }
            catch (Npgsql.NpgsqlException ex)
            {
                _logger.LogError(ex, "outbox drain failed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(300, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(SessionContext context, CancellationToken ct)
    {
        // Read from the lowest *sent* position, not the lowest acknowledged one. A consumer
        // that never acknowledges must not stall delivery of new events to its peers: its sent
        // cursor still advances as events are written to it, so the next page keeps moving.
        var minSent = _bus.MinSentCursor(context.Account.AccountId);
        if (minSent < 0)
        {
            // Nothing connected: rows stay pending on purpose. Retention will expire them
            // and the loss is reported by /health instead of being hidden as "delivered".
            return;
        }

        var pending = await _outbox.FetchAfterAsync(
            context.Account.AccountId, minSent, _options.ReplayBatchSize, ct).ConfigureAwait(false);
        if (pending.Count == 0) return;

        foreach (var row in pending)
        {
            if (!_runtime.IsCurrent(context)) return;
            _bus.Publish(context.Account.AccountId,
                new EventEnvelope(row.Seq, row.EventId, row.EventType, row.Payload));
        }

        // Delivery accounting still uses the acknowledged position: a row is only retired when
        // every connected consumer has acknowledged past it.
        var deliveredThrough = _bus.MinCursor(context.Account.AccountId);
        if (deliveredThrough >= 0)
            await _outbox.MarkDeliveredThroughAsync(context.Account.AccountId, deliveredThrough, ct)
                .ConfigureAwait(false);
    }

    private async Task MaintainAsync(SessionContext context, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextMaintenance) return;
        _nextMaintenance = DateTimeOffset.UtcNow.AddMinutes(1);

        var expired = await _outbox.ExpireAsync(context.Account.AccountId, _options.OutboxRetention, ct)
            .ConfigureAwait(false);
        if (expired > 0)
            _logger.LogWarning("{Count} events expired before any consumer acknowledged them", expired);

        var purged = await _outbox.PurgeAsync(context.Account.AccountId, _options.OutboxRetention, ct)
            .ConfigureAwait(false);
        if (purged > 0) _logger.LogInformation("purged {Count} settled events", purged);
    }
}
