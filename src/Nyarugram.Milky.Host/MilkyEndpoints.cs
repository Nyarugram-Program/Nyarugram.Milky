using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Media;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Host;

/// <summary>
/// Milky HTTP surface: POST /api/{action}, GET/Upgrade /event and the call media socket.
/// Transport-level failures use the status codes Milky defines (401/404/415); every
/// business result is HTTP 200 with a typed envelope.
/// </summary>
public sealed class MilkyEndpoints
{
    private const string ConsumerQuery = "nyarugram_consumer";
    private const string CursorQuery = "nyarugram_cursor";
    private const string ConsumerHeader = "X-Nyarugram-Consumer";

    private readonly MilkyActions _actions;
    private readonly MilkyRuntime _runtime;
    private readonly EventBus _bus;
    private readonly OutboxStore _outbox;
    private readonly IdentityStore _identity;
    private readonly MilkyOptions _options;
    private readonly IMediaEngine _engine;
    private readonly CallStore _calls;
    private readonly RuntimeOptions _runtimeOptions;
    private readonly ILogger<MilkyEndpoints> _logger;

    public MilkyEndpoints(
        MilkyActions actions, MilkyRuntime runtime, EventBus bus, OutboxStore outbox, IdentityStore identity,
        MilkyOptions options, IMediaEngine engine, CallStore calls, RuntimeOptions runtimeOptions,
        ILogger<MilkyEndpoints> logger)
    {
        _actions = actions;
        _runtime = runtime;
        _bus = bus;
        _outbox = outbox;
        _identity = identity;
        _options = options;
        _engine = engine;
        _calls = calls;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    // --------------------------------------------------------------------- /api

    public async Task HandleApiAsync(HttpContext context, string action, CancellationToken ct)
    {
        if (!Authorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        if (!MilkyActions.SupportedActions.Contains(action, StringComparer.Ordinal))
        {
            // Milky reserves HTTP 404 for an API name the implementation does not expose.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var contentType = context.Request.ContentType?.Split(';')[0].Trim();
        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        JsonElement body;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct)
                .ConfigureAwait(false);
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            await WriteAsync(context, MilkyResponse.Failed(MilkyRetCode.BadRequest, "request body is not valid JSON"), ct)
                .ConfigureAwait(false);
            return;
        }

        MilkyResponse response;
        try
        {
            var data = await _actions.HandleAsync(action, body, ct).ConfigureAwait(false);
            response = MilkyResponse.Ok(data);
        }
        catch (MilkyActionException ex)
        {
            response = MilkyResponse.Failed(MapCode(ex.Code), ex.Message);
        }
        catch (Backend.BackendException ex)
        {
            _logger.LogWarning(ex, "backend error on {Action}: {Code} {Status}", action, ex.Code, ex.HttpStatus);
            response = MilkyResponse.Failed(ex.HttpStatus switch
            {
                401 or 403 => MilkyRetCode.NotLoggedIn,
                404 => MilkyRetCode.NotFound,
                409 => MilkyRetCode.Conflict,
                429 => MilkyRetCode.RateLimited,
                _ => MilkyRetCode.Internal,
            }, ex.Code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "unhandled failure in {Action}", action);
            response = MilkyResponse.Failed(MilkyRetCode.Internal, "internal error");
        }

        await WriteAsync(context, response, ct).ConfigureAwait(false);
    }

    private static int MapCode(MilkyErrorCode code) => code switch
    {
        MilkyErrorCode.BadRequest => MilkyRetCode.BadRequest,
        MilkyErrorCode.NotFound => MilkyRetCode.NotFound,
        MilkyErrorCode.NotLoggedIn => MilkyRetCode.NotLoggedIn,
        MilkyErrorCode.Unsupported => MilkyRetCode.Unsupported,
        MilkyErrorCode.Conflict => MilkyRetCode.Conflict,
        MilkyErrorCode.RateLimited => MilkyRetCode.RateLimited,
        MilkyErrorCode.MediaUnavailable => MilkyRetCode.Unsupported,
        MilkyErrorCode.SendOutcomeUnknown => -500,
        MilkyErrorCode.Transient => -503,
        _ => MilkyRetCode.Internal,
    };

    private static async Task WriteAsync(HttpContext context, MilkyResponse response, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, MilkyJson.Options, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------- /event

    /// <summary>
    /// Event delivery with durable replay.
    ///
    /// A consumer may name itself (<c>nyarugram_consumer</c> query or header) and resume from
    /// its stored cursor; SSE additionally honours the standard <c>Last-Event-ID</c> header.
    /// A consumer without an identity starts at the current head and therefore only sees live
    /// events — that boundary is documented rather than hidden.
    /// </summary>
    public async Task HandleEventAsync(HttpContext context, CancellationToken ct)
    {
        if (!Authorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var current = _runtime.Current;
        if (current is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!ExpectedIdentityMatches(context, current))
        {
            // Reject before creating a consumer cursor or subscribing. An event
            // connection must belong to the account the client confirmed.
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = "identity_mismatch",
                    message = "the event connection does not belong to the requested account",
                },
            }).ConfigureAwait(false);
            return;
        }

        var consumerId = ResolveConsumerId(context);
        var startSeq = await ResolveStartSeqAsync(context, current, consumerId, ct).ConfigureAwait(false);
        var gap = await DetectGapAsync(current, startSeq, ct).ConfigureAwait(false);

        // A named consumer's first connection leaves a durable marker at its starting position,
        // so a reconnect that acknowledged nothing still resumes from the same place instead of
        // being treated as a brand-new live-only consumer.
        if (!IsEphemeralConsumer(consumerId))
        {
            await _outbox.EnsureConsumerAsync(current.Account.AccountId, consumerId, startSeq, ct)
                .ConfigureAwait(false);
        }

        // The cursor generation scopes every write for this connection, so a rewind on a newer
        // connection cannot be overwritten by this one's late acknowledgements or shutdown.
        var epoch = IsEphemeralConsumer(consumerId)
            ? 0
            : (await _outbox.GetConsumerCursorEpochAsync(current.Account.AccountId, consumerId, ct)
                .ConfigureAwait(false)).Epoch;

        if (context.WebSockets.IsWebSocketRequest)
            await RunWebSocketAsync(context, current, consumerId, startSeq, epoch, gap, ct).ConfigureAwait(false);
        else
            await RunSseAsync(context, current, consumerId, startSeq, epoch, gap, ct).ConfigureAwait(false);
    }

    private static bool IsEphemeralConsumer(string consumerId)
        => consumerId.StartsWith("ephemeral-", StringComparison.Ordinal);

    /// <summary>
    /// Optional connection-level identity handshake. Admin-mode clients send the
    /// opaque account UUID; no-management clients send the Milky uin. This closes
    /// the race between a client identity query and the event connection.
    /// </summary>
    private static bool ExpectedIdentityMatches(HttpContext context, SessionContext current)
    {
        var expected = context.Request.Query["nyarugram_expected_identity"].ToString();
        if (string.IsNullOrEmpty(expected)) return true;

        if (expected.StartsWith("admin:", StringComparison.Ordinal))
        {
            return Guid.TryParseExact(expected[6..], "N", out var accountId)
                && accountId == current.Account.AccountId;
        }
        if (expected.StartsWith("business:", StringComparison.Ordinal))
        {
            return string.Equals(
                expected[9..],
                current.Account.Uin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
        return false;
    }

    private async Task RunSseAsync(
        HttpContext context, SessionContext current, string consumerId, long startSeq, long epoch,
        EventGap? gap, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        await context.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

        if (gap is { } missing)
            await WriteSseAsync(context, 0, GapPayload(missing), ct).ConfigureAwait(false);

        using var subscription = _bus.Subscribe(
            "sse", consumerId, current.Scope, current.Account.AccountId, startSeq, epoch: epoch);
        using var transport = CancellationTokenSource.CreateLinkedTokenSource(ct, subscription.Stopped);
        ct = transport.Token;
        var persisted = startSeq;
        var pending = 0;
        try
        {
            await foreach (var envelope in subscription.WithCancellation(ct).ConfigureAwait(false))
            {
                // A session switch cancels the subscription; re-check before writing so a late
                // queued event is never delivered to the replaced session.
                if (!_runtime.IsCurrent(current)) break;
                await WriteSseAsync(context, envelope.Seq, envelope.Payload, ct).ConfigureAwait(false);
                subscription.MarkSent(envelope.Seq);
                // SSE has no client acknowledgement; the sent position is the durable cursor.
                subscription.TryAck(envelope.Seq, out _);
                if (++pending >= 25 || envelope.Seq - persisted >= 200)
                {
                    await PersistCursorAsync(current, consumerId, envelope.Seq, subscription.Epoch, ct)
                        .ConfigureAwait(false);
                    persisted = envelope.Seq;
                    pending = 0;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await PersistCursorAsync(current, consumerId, subscription.Cursor, subscription.Epoch, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// WebSocket delivery. Two modes:
    /// - plain: the raw Milky event JSON, acknowledged on send (at-least-once, replayable);
    /// - acknowledged (<c>nyarugram_ws_ack=1</c>): a versioned Nyarugram frame carries the
    ///   sequence and event id, and only an explicit client ack advances the durable cursor.
    /// </summary>
    private async Task RunWebSocketAsync(
        HttpContext context, SessionContext current, string consumerId, long startSeq, long epoch,
        EventGap? gap, CancellationToken ct)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var framed = WantsAcknowledgements(context);

        if (gap is { } missing)
        {
            var gapFrame = framed
                ? JsonSerializer.Serialize(new
                {
                    type = "nyarugram_events_gap",
                    extension_version = 1,
                    epoch,
                    consumer = consumerId,
                    requested_cursor = missing.Requested,
                    oldest_available = missing.OldestAvailable,
                }, MilkyJson.Options)
                : GapPayload(missing);
            await socket.SendAsync(Encoding.UTF8.GetBytes(gapFrame), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }

        using var subscription = _bus.Subscribe(
            "websocket", consumerId, current.Scope, current.Account.AccountId, startSeq, epoch: epoch);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, subscription.Stopped);
        using var abort = subscription.Stopped.Register(socket.Abort);
        using var writer = new SemaphoreSlim(1, 1);
        var receiver = ReceiveAcksAsync(socket, writer, subscription, current, consumerId, framed, linked.Token);

        var persisted = startSeq;
        var pending = 0;
        try
        {
            await foreach (var envelope in subscription.WithCancellation(linked.Token).ConfigureAwait(false))
            {
                if (!_runtime.IsCurrent(current)) break;

                // Serialize data and control frames through one writer. Capture the epoch
                // inside the gate so a resumed frame precedes data from the new epoch.
                await writer.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    var text = framed
                        ? JsonSerializer.Serialize(new
                        {
                            type = "nyarugram_event",
                            extension_version = 1,
                            epoch = subscription.Epoch,
                            seq = envelope.Seq,
                            event_id = envelope.EventId,
                            event_type = envelope.EventType,
                            @event = JsonSerializer.Deserialize<JsonElement>(envelope.Payload, MilkyJson.Options),
                        }, MilkyJson.Options)
                        : envelope.Payload;

                    await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, linked.Token)
                        .ConfigureAwait(false);
                    subscription.MarkSent(envelope.Seq);
                }
                finally
                {
                    writer.Release();
                }

                if (!framed)
                {
                    // Compatibility mode: no ack channel, so the sent position is the cursor.
                    subscription.TryAck(envelope.Seq, out _);
                    pending++;
                }

                if (!framed && (pending >= 25 || envelope.Seq - persisted >= 200))
                {
                    await PersistCursorAsync(current, consumerId, envelope.Seq, subscription.Epoch, ct)
                        .ConfigureAwait(false);
                    persisted = envelope.Seq;
                    pending = 0;
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (WebSocketException ex)
        {
            _logger.LogInformation("event socket closed: {Reason}", ex.Message);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await receiver.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
            // Framed acknowledgements are persisted by the receiver with their explicit
            // epoch. A final snapshot could pair an old cursor with a newly adopted epoch.
            if (!framed)
                await PersistCursorAsync(current, consumerId, subscription.Cursor, subscription.Epoch, CancellationToken.None)
                    .ConfigureAwait(false);
        }
    }

    private static bool WantsAcknowledgements(HttpContext context)
        => (context.Request.Query.TryGetValue("nyarugram_ws_ack", out var value)
            && value.ToString() is "1" or "true")
           || context.Request.Headers.TryGetValue("X-Nyarugram-Ws-Ack", out var header)
           && header.ToString() is "1" or "true";

    /// <summary>
    /// Reads client frames. Only an acknowledgement or an explicit resume request is acted on;
    /// anything else is ignored so a standard client cannot accidentally corrupt the cursor.
    /// </summary>
    private async Task ReceiveAcksAsync(
        WebSocket socket, SemaphoreSlim writer, EventSubscription subscription, SessionContext current, string consumerId,
        bool framed, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[2048];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // The client dropped the connection without a close handshake (a hard abort).
                    return;
                }
                if (received.MessageType == WebSocketMessageType.Close) return;

                if (!framed)
                {
                    // Drain unsolicited client payloads; the raw protocol has no client frames.
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var type)) continue;
                    var action = type.GetString();
                    if (action is not ("nyarugram_ack" or "nyarugram_resume")) continue;
                    if (!root.TryGetProperty("epoch", out var epochValue)
                        || epochValue.ValueKind != JsonValueKind.Number
                        || !epochValue.TryGetInt64(out var requestEpoch)
                        || requestEpoch != subscription.Epoch)
                    {
                        await SendFrameAsync(socket, writer, new
                        {
                            type = action + "_rejected",
                            extension_version = 1,
                            epoch = subscription.Epoch,
                            reason = "epoch is required and must match the current recovery generation",
                        }, ct).ConfigureAwait(false);
                        continue;
                    }
                    switch (action)
                    {
                        case "nyarugram_ack":
                        {
                            if (!root.TryGetProperty("seq", out var seqValue) || !seqValue.TryGetInt64(out var seq))
                                continue;
                            // The event write publishes SentCursor under this same gate.
                            // A fast client's ACK must not race that publication.
                            bool accepted;
                            string reason;
                            await writer.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                accepted = subscription.TryAck(seq, out reason);
                            }
                            finally
                            {
                                writer.Release();
                            }
                            if (accepted)
                            {
                                await PersistCursorAsync(current, consumerId, seq, requestEpoch, ct)
                                    .ConfigureAwait(false);
                            }
                            else if (reason.Contains("ahead", StringComparison.Ordinal))
                            {
                                await SendFrameAsync(socket, writer, new
                                {
                                    type = "nyarugram_ack_rejected",
                                    extension_version = 1,
                                    epoch = requestEpoch,
                                    seq,
                                    reason,
                                }, ct).ConfigureAwait(false);
                            }
                            break;
                        }
                        case "nyarugram_resume":
                        {
                            if (!root.TryGetProperty("cursor", out var cursorValue)
                                || !cursorValue.TryGetInt64(out var cursor))
                                continue;
                            if (subscription.TryResume(cursor, out var reason))
                            {
                                // A resume is an explicit rewind: lower the durable position and bump
                                // the cursor generation, so any stale writer from a previous epoch is
                                // ignored. The subscription adopts the new generation.
                                long resumeTarget;
                                await writer.WaitAsync(ct).ConfigureAwait(false);
                                try
                                {
                                    var newEpoch = await _outbox.RewindConsumerCursorAsync(
                                        current.Account.AccountId, consumerId, cursor, ct).ConfigureAwait(false);
                                    subscription.AdoptEpoch(newEpoch);
                                    resumeTarget = subscription.SentCursor;
                                    var response = JsonSerializer.SerializeToUtf8Bytes(new
                                    {
                                        type = "nyarugram_resumed",
                                        extension_version = 1,
                                        epoch = newEpoch,
                                        cursor,
                                    }, MilkyJson.Options);
                                    await socket.SendAsync(response, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                                }
                                finally
                                {
                                    writer.Release();
                                }
                                // Redeliver the events between the new cursor and what was already
                                // sent. The range is paged so a recovery wider than one batch is
                                // replayed in full rather than truncated at the first page.
                                var replayFrom = cursor;
                                while (replayFrom < resumeTarget && !ct.IsCancellationRequested)
                                {
                                    var page = await _outbox.FetchAfterAsync(
                                        subscription.AccountId, replayFrom, _runtimeOptions.ReplayBatchSize, ct)
                                        .ConfigureAwait(false);
                                    if (page.Count == 0) break;
                                    var within = page.Where(r => r.Seq <= resumeTarget).ToList();
                                    subscription.Replay(within
                                        .Select(r => new EventEnvelope(r.Seq, r.EventId, r.EventType, r.Payload))
                                        .ToList());
                                    if (within.Count == 0 || within[^1].Seq >= resumeTarget) break;
                                    replayFrom = within[^1].Seq;
                                }
                            }
                            else
                            {
                                await SendFrameAsync(socket, writer, new
                                {
                                    type = "nyarugram_resume_rejected",
                                    extension_version = 1,
                                    epoch = subscription.Epoch,
                                    cursor,
                                    reason,
                                }, ct).ConfigureAwait(false);
                            }
                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            subscription.Cancel();
        }
    }

    private static async Task SendFrameAsync(
        WebSocket socket, SemaphoreSlim writer, object frame, CancellationToken ct)
    {
        await writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, MilkyJson.Options);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            writer.Release();
        }
    }

    private static async Task WriteSseAsync(HttpContext context, long seq, string payload, CancellationToken ct)
    {
        var frame = seq > 0
            ? $"id: {seq}\nevent: milky_event\ndata: {payload}\n\n"
            : $"event: milky_event\ndata: {payload}\n\n";
        await context.Response.WriteAsync(frame, ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string GapPayload(EventGap gap) => JsonSerializer.Serialize(new
    {
        time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        event_type = "nyarugram_events_gap",
        data = new
        {
            consumer = gap.ConsumerId,
            requested_cursor = gap.Requested,
            oldest_available = gap.OldestAvailable,
            message = "events older than the retention window were expired; resynchronise state via the backend difference API",
        },
    }, MilkyJson.Options);

    private sealed record EventGap(string ConsumerId, long Requested, long OldestAvailable);

    private static string ResolveConsumerId(HttpContext context)
    {
        if (context.Request.Query.TryGetValue(ConsumerQuery, out var query) && query.Count > 0
            && query[0] is { Length: > 0 and <= 128 } named)
            return named;
        if (context.Request.Headers.TryGetValue(ConsumerHeader, out var header)
            && header.ToString() is { Length: > 0 and <= 128 } fromHeader)
            return fromHeader;
        return "ephemeral-" + Guid.NewGuid().ToString("N");
    }

    private async Task<long> ResolveStartSeqAsync(
        HttpContext context, SessionContext current, string consumerId, CancellationToken ct)
    {
        // Explicit cursor wins: SSE Last-Event-ID, then query parameter.
        if (context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && long.TryParse(lastEventId.ToString(), out var fromHeader))
            return fromHeader;
        if (context.Request.Query.TryGetValue(CursorQuery, out var cursor)
            && long.TryParse(cursor.ToString(), out var fromQuery))
            return fromQuery;

        var stored = await _outbox.GetConsumerCursorAsync(current.Account.AccountId, consumerId, ct)
            .ConfigureAwait(false);
        if (stored >= 0) return stored;
        // New consumer without a cursor: live-only, so it does not replay the whole history.
        return await _outbox.LatestSeqAsync(current.Account.AccountId, ct).ConfigureAwait(false);
    }

    private async Task<EventGap?> DetectGapAsync(SessionContext current, long startSeq, CancellationToken ct)
    {
        if (startSeq <= 0) return null;
        var oldest = await _outbox.OldestReplayableSeqAsync(current.Account.AccountId, ct).ConfigureAwait(false);
        if (oldest == 0 || startSeq + 1 >= oldest) return null;
        return new EventGap("", startSeq, oldest - 1);
    }

    private async Task PersistCursorAsync(
        SessionContext current, string consumerId, long seq, long epoch, CancellationToken ct)
    {
        if (seq <= 0) return;
        try
        {
            await _outbox.SetConsumerCursorAsync(current.Account.AccountId, consumerId, seq, epoch, ct)
                .ConfigureAwait(false);
        }
        catch (Npgsql.NpgsqlException ex)
        {
            _logger.LogWarning(ex, "could not persist consumer cursor {Consumer}", consumerId);
        }
    }

    // ------------------------------------------------------------ call media WS

    /// <summary>
    /// Application audio socket. Only a live engine session that exposes an application audio
    /// channel can be opened; the endpoint has no knowledge of any concrete engine.
    /// </summary>
    public async Task HandleMediaAsync(HttpContext context, string callId, CancellationToken ct)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!TryGetToken(context, out var token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var current = _runtime.Current;
        if (current is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var resolved = await _calls.ResolveMediaTokenAsync(CallStore.HashToken(token), ct).ConfigureAwait(false);
        if (resolved is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!Guid.TryParse(callId, out var id) || resolved.Value.CallId != id)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (resolved.Value.AccountId != current.Account.AccountId)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (resolved.Value.Generation != current.Generation)
        {
            // A media token issued for an earlier login generation is dead after a switch.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var session = _engine.Find(id);
        var audio = session?.ApplicationAudio;
        if (session is null || audio is null)
        {
            // No engine session means no media socket; never fake a working audio path.
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(
                new { error = "media_unavailable", reason = _engine.Capability.Reason }, ct).ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        _logger.LogInformation("media socket opened for call {CallId} engine={Engine}",
            id, _engine.Capability.Engine);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteOutputAsync(socket, audio, linked.Token);
        try
        {
            await ReadInputAsync(socket, audio, linked.Token).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _logger.LogInformation("media socket closed for call {CallId}", id);
        }
    }

    private static async Task ReadInputAsync(
        WebSocket socket, IApplicationAudioChannel audio, CancellationToken ct)
    {
        var buffer = new byte[PcmAudio.MaxFrameBytes * 2];
        var assembled = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close) return;
            assembled.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage) continue;

            if (!PcmAudio.TryParse(assembled.GetBuffer().AsSpan(0, (int)assembled.Length), out var frame, out var error))
            {
                await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, error, ct).ConfigureAwait(false);
                return;
            }
            if (!frame.Input)
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    "application frames must set the input flag", ct).ConfigureAwait(false);
                return;
            }
            audio.WriteApplicationAudio(frame);
            assembled.SetLength(0);
        }
    }

    private static async Task WriteOutputAsync(
        WebSocket socket, IApplicationAudioChannel audio, CancellationToken ct)
    {
        await foreach (var frame in audio.ReadPeerAudioAsync(ct).ConfigureAwait(false))
        {
            var bytes = PcmAudio.BuildFrame(frame.Sequence, frame.TimestampMs, false, frame.Payload);
            await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------- health

    public async Task HandleHealthAsync(HttpContext context, MilkyDatabase database, CancellationToken ct)
    {
        var databaseOk = await database.HealthyAsync(ct).ConfigureAwait(false);
        var current = _runtime.Current;
        var status = databaseOk && current is not null ? "ok" : databaseOk ? "degraded" : "unhealthy";

        long pending = 0, expired = 0;
        DateTimeOffset? oldest = null;
        if (databaseOk && current is not null)
        {
            var metrics = await _outbox.GetMetricsAsync(current.Account.AccountId, ct).ConfigureAwait(false);
            pending = metrics.Pending;
            expired = metrics.Expired;
            oldest = metrics.OldestPendingAt;
        }

        context.Response.StatusCode = databaseOk ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            status,
            storage = databaseOk ? "postgresql" : "unavailable",
            logged_in = current is not null,
            uin = current?.Account.Uin,
            generation = current?.Generation,
            instance = _runtime.InstanceId,
            media_engine = _engine.Capability.Engine,
            media_available = _engine.Capability.Available,
            event_subscribers = _bus.Count,
            event_consumers = current is null ? [] : _bus.Consumers(current.Account.AccountId),
            outbox_pending = pending,
            outbox_expired = expired,
            outbox_oldest_pending = oldest,
        }, ct).ConfigureAwait(false);
    }

    // --------------------------------------------------------------------- auth

    private bool Authorized(HttpContext context)
    {
        if (!TryGetToken(context, out var token)) return false;
        var expected = Encoding.UTF8.GetBytes(_options.AccessToken);
        var actual = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool TryGetToken(HttpContext context, out string token)
    {
        token = "";
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = header[7..];
            return token.Length > 0;
        }
        if (context.Request.Query.TryGetValue("access_token", out var query) && query.Count > 0)
        {
            token = query[0] ?? "";
            return token.Length > 0;
        }
        return false;
    }
}
