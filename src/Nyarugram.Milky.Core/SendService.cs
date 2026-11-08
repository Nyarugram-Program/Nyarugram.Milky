using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Storage;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

public sealed record SendOutcome(long MessageSeq, long Time);

/// <summary>
/// Durable message sending.
///
/// The operation row — account scope, frozen Protobuf request, request digest, full signed
/// random id and stage — is committed before the backend is contacted. A retry after a
/// timeout, a dropped response or a process restart therefore replays the same random id and
/// the same content, which the backend treats as one logical send.
///
/// Without an application-supplied key the operation is still durable, but the caller cannot
/// name it again: a fresh HTTP request is a new logical send. We never merge two intentional
/// identical messages by hashing their text.
/// </summary>
public sealed class SendService
{
    public const string Scope = "send_message";

    private readonly BackendRpc _rpc;
    private readonly IdentityResolver _identities;
    private readonly MessageMapper _mapper;
    private readonly IdempotencyStore _operations;
    private readonly MilkyRuntime _runtime;
    private readonly RuntimeOptions _options;
    private readonly ILogger<SendService> _logger;

    public SendService(
        BackendRpc rpc, IdentityResolver identities, MessageMapper mapper, IdempotencyStore operations,
        MilkyRuntime runtime, RuntimeOptions options, ILogger<SendService> logger)
    {
        _rpc = rpc;
        _identities = identities;
        _mapper = mapper;
        _operations = operations;
        _runtime = runtime;
        _options = options;
        _logger = logger;
    }

    public async Task<SendOutcome> SendAsync(
        SessionContext context, PeerKind kind, long peerUin, IReadOnlyList<MessageSegment> segments,
        string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is { Length: > 128 })
            throw new MilkyActionException(MilkyErrorCode.BadRequest, "nyarugram_idempotency_key must be 1..128 characters");
        if (idempotencyKey is { Length: 0 })
            throw new MilkyActionException(MilkyErrorCode.BadRequest, "nyarugram_idempotency_key must not be empty");

        var peer = await _identities.RequirePeerAsync(context, kind, peerUin, ct).ConfigureAwait(false);
        var composition = await _mapper.ComposeAsync(context, segments, ct).ConfigureAwait(false);

        var request = new P.RpcRequest
        {
            SendMessage = new P.SendMessageRequest
            {
                Peer = peer,
                RandomId = NewRandomId(),
                Text = composition.Text,
                Silent = false,
            },
        };
        if (composition.ReplyToMessageId is { } reply) request.SendMessage.ReplyToMessageId = reply;
        request.SendMessage.Entities.AddRange(composition.Entities);

        var requestHash = HashRequest(kind, peerUin, composition);
        var key = idempotencyKey ?? "auto:" + request.SendMessage.RandomId.ToString("x");
        var requestJson = JsonSerializer.Serialize(new { request = Convert.ToBase64String(request.ToByteArray()) });

        (IdempotencyRecord record, bool created) claimed;
        try
        {
            claimed = await _operations
                .ClaimAsync(context.Account.AccountId, Scope, key, requestHash, request.SendMessage.RandomId, requestJson, ct)
                .ConfigureAwait(false);
        }
        catch (IdempotencyConflictException)
        {
            throw new MilkyActionException(MilkyErrorCode.Conflict,
                $"idempotency key '{key}' was already used with a different payload");
        }
        var (record, created) = claimed;

        if (!created)
        {
            if (record.Completed && record.ResultJson is { } result) return ParseResult(result);
            if (record.State == IdempotencyStates.Failed)
                throw new MilkyActionException(MilkyErrorCode.Conflict,
                    "a previous attempt with this idempotency key failed definitively; use a new key");
            // Replay the frozen request, never a regenerated one.
            request = P.RpcRequest.Parser.ParseFrom(
                Convert.FromBase64String(ExtractRequest(requestJson: record.RequestJson, fallback: requestJson)));
        }

        var lease = await _operations
            .TryLeaseAsync(context.Account.AccountId, Scope, key, _options.OperationLease, ct)
            .ConfigureAwait(false);
        if (lease is null) return await WaitForCompletionAsync(context, key, ct).ConfigureAwait(false);

        try
        {
            return await DriveAsync(context, key, lease.Value, request, ct).ConfigureAwait(false);
        }
        finally
        {
            await _operations.ReleaseLeaseAsync(
                context.Account.AccountId, Scope, key, lease.Value, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the frozen request with bounded retries on the same random id. Returns a typed
    /// outcome: definite rejection, success, or "unknown" when the transport cannot tell.
    /// </summary>
    private async Task<SendOutcome> DriveAsync(
        SessionContext context, string key, Guid lease, P.RpcRequest request, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Losing the lease means another executor owns this operation now; stop
                // instead of writing a state the winner would have to undo.
                if (!await _operations.SetStateAsync(context.Account.AccountId, Scope, key, lease,
                        IdempotencyStates.Submitted, null, ct).ConfigureAwait(false))
                    throw new MilkyActionException(MilkyErrorCode.Transient,
                        "the operation lease was taken over by another executor");

                var response = await _rpc.CallAsync(context, request, ct).ConfigureAwait(false);
                var sent = response.SendMessage
                    ?? throw new MilkyActionException(MilkyErrorCode.Internal, "backend returned no message");

                // The response may carry an Updates block with a pts. That pts belongs to the
                // send event, not to the synchronisation cursor, and is deliberately ignored.
                var outcome = new SendOutcome(sent.Message.MessageId, sent.Message.CreatedAtMs / 1000);
                var stored = await _operations.CompleteAsync(context.Account.AccountId, Scope, key, lease,
                    JsonSerializer.Serialize(outcome), ct).ConfigureAwait(false);
                if (!stored)
                {
                    // Another executor already completed or took over. Its result wins.
                    var current = await _operations.GetAsync(context.Account.AccountId, Scope, key, ct)
                        .ConfigureAwait(false);
                    if (current is { Completed: true, ResultJson: { } existing }) return ParseResult(existing);
                    throw new MilkyActionException(MilkyErrorCode.Transient,
                        "the operation lease was taken over before the result was stored");
                }
                return outcome;
            }
            catch (BackendException ex) when (ex.Retryable && !ex.OutcomeUnknown && attempt < 2)
            {
                _logger.LogWarning("send retry {Attempt} with random_id={RandomId}: {Code}",
                    attempt + 1, request.SendMessage.RandomId, ex.Code);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
            catch (BackendException ex) when (ex.OutcomeUnknown)
            {
                // The message may be committed. Keep the operation reconcilable instead of
                // reporting a failure the application could "retry" into a duplicate.
                await _operations.SetStateAsync(context.Account.AccountId, Scope, key, lease,
                    IdempotencyStates.Unknown, ex.Code, ct).ConfigureAwait(false);
                throw new MilkyActionException(MilkyErrorCode.SendOutcomeUnknown,
                    $"submission outcome unknown ({ex.Code}); reconcile with idempotency key '{key}' or wait for automatic reconciliation");
            }
            catch (BackendException ex)
            {
                await _operations.SetStateAsync(context.Account.AccountId, Scope, key, lease,
                    IdempotencyStates.Failed, ex.Code, ct).ConfigureAwait(false);
                throw Map(ex);
            }
        }

        await _operations.SetStateAsync(context.Account.AccountId, Scope, key, lease,
            IdempotencyStates.Unknown, "retries_exhausted", ct).ConfigureAwait(false);
        throw new MilkyActionException(MilkyErrorCode.SendOutcomeUnknown,
            $"submission outcome unknown after retries; reconcile with idempotency key '{key}'");
    }

    /// <summary>Another request owns the lease: wait for its result instead of sending twice.</summary>
    private async Task<SendOutcome> WaitForCompletionAsync(SessionContext context, string key, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _options.OperationWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var record = await _operations.GetAsync(context.Account.AccountId, Scope, key, ct).ConfigureAwait(false);
            if (record is null)
                throw new MilkyActionException(MilkyErrorCode.Transient, "operation record disappeared");
            if (record.Completed && record.ResultJson is { } result) return ParseResult(result);
            if (record.State == IdempotencyStates.Failed)
                throw new MilkyActionException(MilkyErrorCode.Conflict,
                    "a concurrent attempt with this idempotency key failed");
            await Task.Delay(120, ct).ConfigureAwait(false);
        }
        throw new MilkyActionException(MilkyErrorCode.Transient,
            "the same idempotency key is still in progress; retry shortly to read its result");
    }

    /// <summary>
    /// Reconciles operations whose outcome was never observed (dropped response, crash).
    /// Replaying the frozen random id is safe: the backend dedupes it.
    /// </summary>
    public async Task<int> ReconcileAsync(SessionContext context, CancellationToken ct)
    {
        var unresolved = await _operations.ListUnresolvedAsync(context.Account.AccountId, Scope, 20, ct)
            .ConfigureAwait(false);
        var resolved = 0;
        foreach (var (key, record) in unresolved)
        {
            if (record.RequestJson is null) continue;
            if (!_runtime.IsCurrent(context)) return resolved;
            var lease = await _operations
                .TryLeaseAsync(context.Account.AccountId, Scope, key, _options.OperationLease, ct)
                .ConfigureAwait(false);
            if (lease is null) continue;
            try
            {
                var request = P.RpcRequest.Parser.ParseFrom(
                    Convert.FromBase64String(ExtractRequest(record.RequestJson, "")));
                var response = await _rpc.CallAsync(context, request, ct).ConfigureAwait(false);
                var sent = response.SendMessage;
                if (sent is null) continue;
                await _operations.CompleteAsync(context.Account.AccountId, Scope, key, lease.Value,
                    JsonSerializer.Serialize(new SendOutcome(sent.Message.MessageId, sent.Message.CreatedAtMs / 1000)), ct)
                    .ConfigureAwait(false);
                _logger.LogInformation("reconciled send {Key} as message {MessageId}", key, sent.Message.MessageId);
                resolved++;
            }
            catch (BackendException ex) when (ex.Retryable || ex.OutcomeUnknown)
            {
                _logger.LogDebug("send {Key} still unresolved: {Code}", key, ex.Code);
            }
            catch (BackendException ex)
            {
                await _operations.SetStateAsync(context.Account.AccountId, Scope, key, lease.Value,
                    IdempotencyStates.Failed, ex.Code, ct).ConfigureAwait(false);
            }
            finally
            {
                await _operations.ReleaseLeaseAsync(
                    context.Account.AccountId, Scope, key, lease.Value, CancellationToken.None).ConfigureAwait(false);
            }
        }
        return resolved;
    }

    private static string HashRequest(PeerKind kind, long peerUin, OutgoingComposition composition)
    {
        var builder = new StringBuilder();
        builder.Append(kind == PeerKind.Group ? "group" : "friend").Append('|').Append(peerUin).Append('|');
        builder.Append(composition.Text).Append('|').Append(composition.ReplyToMessageId?.ToString() ?? "-").Append('|');
        foreach (var entity in composition.Entities.OrderBy(e => e.OffsetUtf16))
            builder.Append(entity.OffsetUtf16).Append(':').Append(entity.LengthUtf16).Append(':')
                .Append(entity.KindCase).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string ExtractRequest(string? requestJson, string fallback)
    {
        if (string.IsNullOrEmpty(requestJson)) return fallback;
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            return document.RootElement.TryGetProperty("request", out var value)
                ? value.GetString() ?? fallback
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static SendOutcome ParseResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return new SendOutcome(
                document.RootElement.GetProperty("MessageSeq").GetInt64(),
                document.RootElement.GetProperty("Time").GetInt64());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            throw new MilkyActionException(MilkyErrorCode.Internal, "stored send result is unreadable");
        }
    }

    internal static MilkyActionException Map(BackendException ex) => ex.HttpStatus switch
    {
        401 or 403 => new MilkyActionException(MilkyErrorCode.NotLoggedIn, ex.Code),
        404 => new MilkyActionException(MilkyErrorCode.NotFound, ex.Code),
        409 => new MilkyActionException(MilkyErrorCode.Conflict, ex.Code),
        429 => new MilkyActionException(MilkyErrorCode.RateLimited, ex.Code),
        501 => new MilkyActionException(MilkyErrorCode.Unsupported, ex.Code),
        400 => new MilkyActionException(MilkyErrorCode.BadRequest, ex.Code),
        _ => new MilkyActionException(MilkyErrorCode.Transient, ex.Code),
    };

    /// <summary>Full signed 64-bit id; zero is not a valid correlation value.</summary>
    internal static long NewRandomId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        var value = BitConverter.ToInt64(buffer);
        return value == 0 ? 1 : value;
    }
}

/// <summary>Retries sends whose outcome was never observed.</summary>
public sealed class SendReconciler : BackgroundService
{
    private readonly MilkyRuntime _runtime;
    private readonly SendService _sends;
    private readonly RuntimeOptions _options;
    private readonly ILogger<SendReconciler> _logger;

    public SendReconciler(
        MilkyRuntime runtime, SendService sends, RuntimeOptions options, ILogger<SendReconciler> logger)
    {
        _runtime = runtime;
        _sends = sends;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.BackgroundSendReconcile)
        {
            _logger.LogWarning("automatic send reconciliation is disabled by configuration");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var context = _runtime.Current;
            if (context is null)
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                continue;
            }
            try
            {
                var resolved = await _sends.ReconcileAsync(context, stoppingToken).ConfigureAwait(false);
                if (resolved > 0) _logger.LogInformation("reconciled {Count} pending sends", resolved);
            }
            catch (Exception ex) when (ex is BackendException or Npgsql.NpgsqlException or MilkyActionException)
            {
                _logger.LogDebug(ex, "send reconciliation pass skipped");
            }
            await Task.Delay(_options.ReconcileInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
