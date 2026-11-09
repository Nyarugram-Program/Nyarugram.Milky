using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Storage;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Call control plane. Maps Milky extension requests onto backend phone.* operations and
/// keeps a durable, versioned projection.
///
/// Guarantees:
/// - the start operation (random id, g_a hash, encrypted DH secret) is persisted before the
///   backend is contacted, so a crash or a dropped response resumes the same call;
/// - the accept material (private key and g_b) is persisted before accept_call;
/// - the callee verifies the caller's original g_a commitment, the public value range and the
///   final fingerprint before any media engine starts;
/// - state, media and hangup transitions write their outbox row in the same transaction;
/// - media recovery is independent of the signalling rank, bounded and single-flight;
/// - a media engine that dies after readiness ends the call and emits media_closed;
/// - engine signaling is delivered by a bounded, ordered relay with bounded retries;
/// - an unknown hangup result is never reported as a released line.
/// </summary>
public sealed class CallService : ICallUpdateSink, IAsyncDisposable
{
    public const string StartScope = "call_start";

    private readonly MilkyRuntime _runtime;
    private readonly BackendRpc _rpc;
    private readonly IdentityResolver _identities;
    private readonly CallStore _calls;
    private readonly IdempotencyStore _idempotency;
    private readonly SecretProtector _protector;
    private readonly IDiffieHellman _dh;
    private readonly IMediaEngine _engine;
    private readonly RuntimeOptions _options;
    private readonly ILogger<CallService> _logger;

    /// <summary>Derived and fully verified shared keys, keyed by adapter call id.</summary>
    private readonly ConcurrentDictionary<Guid, byte[]> _verifiedKeys = new();
    private readonly ConcurrentDictionary<Guid, byte> _mediaStarting = new();

    /// <summary>Serialises work per call so one call never starts two engines.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _callLocks = new();

    /// <summary>
    /// One outbound signaling relay per live media instance, keyed by adapter call id.
    /// A replaced instance is removed before its replacement is registered, so signals
    /// produced by an old agent can never reach the backend on behalf of a new one.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SignalingBinding> _signals = new();

    /// <summary>
    /// Faults whose in-memory resources were already stopped but whose database
    /// finalization (failed state, media_closed, token revocation, line release) could
    /// not complete. Entries are retried with backoff by <see cref="CallFaultFinalizer"/>.
    /// This is an in-memory queue; a process restart relies on startup reconciliation of
    /// the previous instance id instead of replaying these entries.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, PendingFault> _pendingFaults = new();

    internal sealed class SignalingBinding
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
        public required Guid AccountId { get; init; }
        public required long Generation { get; init; }
        public SignalingRelay? Relay { get; set; }
        public IMediaSession? Session { get; set; }
    }

    private sealed class PendingFault
    {
        public required SessionContext Context { get; init; }
        public required Guid CallId { get; init; }
        public required string Reason { get; init; }
        public IMediaSession? Media { get; init; }
        public SignalingBinding? Binding { get; init; }
        public int Attempts { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; }
    }

    // Test seams: deterministic gates/faults for the finalization and stale-callback
    // regressions. They are null in production.
    internal Func<Guid, Task>? BeforeSignalingFailureLockForTests { get; set; }
    internal Func<string, Exception?>? FinalizationFaultForTests { get; set; }
    internal int PendingFaultCountForTests => _pendingFaults.Count;

    internal SignalingBinding? SignalingBindingForTests(Guid callId)
        => _signals.TryGetValue(callId, out var binding) ? binding : null;

    internal async Task InstallReplacementSignalingForTestsAsync(
        SessionContext context, CallRecord call, long backendCallId, IMediaSession session)
    {
        var gate = LockFor(call.CallId);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopSignalingAsync(call.CallId).ConfigureAwait(false);
            var binding = BeginSignaling(context, call, backendCallId);
            binding.Session = session;
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task ProcessPendingFaultsForTestsAsync(CancellationToken ct)
        => ProcessPendingFaultsAsync(ct);

    public CallService(
        MilkyRuntime runtime, BackendRpc rpc, IdentityResolver identities, CallStore calls,
        IdempotencyStore idempotency, SecretProtector protector, IDiffieHellman dh,
        IMediaEngine engine, RuntimeOptions options, ILogger<CallService> logger)
    {
        _runtime = runtime;
        _rpc = rpc;
        _identities = identities;
        _calls = calls;
        _idempotency = idempotency;
        _protector = protector;
        _dh = dh;
        _engine = engine;
        _options = options;
        _logger = logger;
    }

    public MediaCapability Capability => _engine.Capability;

    // ------------------------------------------------------------------ commands

    public async Task<CallInfo> StartAsync(SessionContext context, StartCallInput input, CancellationToken ct)
    {
        if (input.RequestId is not { Length: >= 1 and <= 64 })
            throw new MilkyActionException(MilkyErrorCode.BadRequest, "request_id is required (1..64 chars)");
        if (input.Video && !Capability.Video)
            throw new MilkyActionException(MilkyErrorCode.MediaUnavailable,
                "video calls are not supported by this build");
        if (!Capability.Available && !Capability.RingOnly)
            throw new MilkyActionException(MilkyErrorCode.MediaUnavailable, Capability.Reason!);
        if (!Capability.HasProtocolProposal)
            throw new MilkyActionException(MilkyErrorCode.MediaUnavailable,
                "media engine exposes no call protocol proposal; the backend would reject the offer");

        var peer = await _identities.RequireUserAsync(context, input.UserId, ct).ConfigureAwait(false);
        var requestHash = Hash($"{input.UserId}|{input.Video}");

        var existingOperation = await _calls.GetOperationAsync(context.Account.AccountId, input.RequestId, ct)
            .ConfigureAwait(false);
        if (existingOperation is not null)
        {
            if (!string.Equals(existingOperation.RequestHash, requestHash, StringComparison.Ordinal))
                throw new MilkyActionException(MilkyErrorCode.Conflict,
                    "request_id was already used with a different callee or video flag");
            if (existingOperation.CallId is { } existingCallId)
            {
                var existingCall = await _calls.GetAsync(context.Account.AccountId, existingCallId, ct)
                    .ConfigureAwait(false);
                if (existingCall is not null) return ToInfo(existingCall);
            }
        }

        var dh = await RequireDhConfigAsync(context, ct).ConfigureAwait(false);
        var (secret, publicHash, protection) = existingOperation is null
            ? CreateKeyMaterial(dh)
            : MaterialFrom(existingOperation);

        var (operation, _) = await _calls.ClaimOperationAsync(
            context.Account.AccountId, input.RequestId, requestHash,
            existingOperation?.RandomId ?? NewRandomId(), publicHash, protection, ct).ConfigureAwait(false);

        if (operation.CallId is { } linkedCallId)
        {
            var linked = await _calls.GetAsync(context.Account.AccountId, linkedCallId, ct).ConfigureAwait(false);
            if (linked is not null) return ToInfo(linked);
        }

        var lease = await _calls.TryLeaseOperationAsync(
            context.Account.AccountId, input.RequestId, _options.OperationLease, ct).ConfigureAwait(false);
        if (lease is null)
        {
            var waited = await WaitForCallAsync(context, input.RequestId, ct).ConfigureAwait(false);
            if (waited is not null) return ToInfo(waited);
            throw new MilkyActionException(MilkyErrorCode.Transient,
                "the same request_id is still being processed; retry shortly");
        }

        try
        {
            var result = await _rpc.CallAsync(context, new P.RpcRequest
            {
                RequestCall = new P.PhoneRequestCallRequest
                {
                    User = new P.UserSelector { User = peer.Identity },
                    RandomId = operation.RandomId,
                    Video = input.Video,
                    GAHash = ByteString.CopyFrom(operation.GaHash),
                    Protocol = BuildProtocol(),
                },
            }, ct).ConfigureAwait(false);

            var backendCall = result.RequestCall?.PhoneCall
                ?? throw new MilkyActionException(MilkyErrorCode.Transient, "backend returned no call");

            var call = await _calls.GetByBackendAsync(
                context.Account.AccountId, backendCall.CallId, ct).ConfigureAwait(false);

            if (call is null)
            {
                call = await _calls.CreateWithEventAsync(
                    context.Account.AccountId, context.Generation, _runtime.InstanceId, "outgoing",
                    input.UserId, input.Video, CallStates.Outgoing,
                    record => BuildEvent(context, NyarugramEventType.CallStateChanged, record, null),
                    ct).ConfigureAwait(false);
                call = await _calls.AttachBackendAsync(
                    call.CallId, backendCall.CallId, backendCall.AccessHash, _runtime.InstanceId, ct)
                    .ConfigureAwait(false);
            }

            await _calls.SaveSecretAsync(call.CallId, operation.Secret.Cipher, operation.Secret.Nonce,
                operation.Secret.Tag, ct).ConfigureAwait(false);
            await _calls.SaveSnapshotAsync(call.CallId, backendCall.ToByteArray(), ct).ConfigureAwait(false);

            if (!await _calls.UpdateOperationStageAsync(context.Account.AccountId, input.RequestId, lease.Value,
                    "linked", call.CallId, null, ct).ConfigureAwait(false))
                _logger.LogWarning("call start lease for {Key} was taken over before linking", input.RequestId);

            return ToInfo(call);
        }
        catch (IdempotencyConflictException)
        {
            throw new MilkyActionException(MilkyErrorCode.Conflict,
                "request_id was already used with different call parameters");
        }
        catch (BackendException ex)
        {
            await _calls.UpdateOperationStageAsync(context.Account.AccountId, input.RequestId, lease.Value,
                ex.Retryable ? "prepared" : "failed", null, ex.Code, ct).ConfigureAwait(false);
            throw SendService.Map(ex);
        }
        finally
        {
            await _calls.ReleaseOperationLeaseAsync(
                context.Account.AccountId, input.RequestId, lease.Value, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<CallInfo> AcceptAsync(SessionContext context, string callId, CancellationToken ct)
    {
        var call = await RequireAsync(context, callId, ct).ConfigureAwait(false);
        if (call.Direction != "incoming")
            throw new MilkyActionException(MilkyErrorCode.Conflict, "only an incoming call can be accepted");
        if (call.IsTerminal)
            throw new MilkyActionException(MilkyErrorCode.Conflict, "call already finished");
        if (!Capability.Available)
            throw new MilkyActionException(MilkyErrorCode.MediaUnavailable, Capability.Reason!);
        if (!Capability.HasProtocolProposal)
            throw new MilkyActionException(MilkyErrorCode.MediaUnavailable,
                "media engine exposes no call protocol proposal");
        if (call.BackendCallId is not { } backendCallId)
            throw new MilkyActionException(MilkyErrorCode.Conflict, "call has no backend identity yet");

        var gate = LockFor(call.CallId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            call = await RequireAsync(context, callId, ct).ConfigureAwait(false);
            if (call.IsTerminal) return ToInfo(call);

            var dh = await RequireDhConfigAsync(context, ct).ConfigureAwait(false);

            // Persist the key material before accept_call: a lost response or a restart must
            // resubmit this exact g_b, never a freshly generated one.
            var material = await _calls.ClaimAcceptAsync(call.CallId, context.Account.AccountId, () =>
            {
                var secret = _dh.RandomSecret(256);
                var gb = _dh.PublicValue(dh.Value.Config.P.ToByteArray(), dh.Value.Config.G, secret);
                DhValidation.ValidatePublicValue(gb, dh.Value.Config.P.ToByteArray(), "CALL_GB");
                var (cipher, nonce, tag) = _protector.Protect(Convert.ToBase64String(secret));
                return (new CallSecret(cipher, nonce, tag), gb);
            }, ct).ConfigureAwait(false);

            if (call.AcceptStage == AcceptStages.Accepted) return ToInfo(call);

            var reference = new P.PhoneCallRef { CallId = backendCallId, AccessHash = call.AccessHash ?? 0 };
            P.PhoneCallResult? accepted;
            try
            {
                var acceptResponse = await _rpc.CallAsync(context, new P.RpcRequest
                {
                    AcceptCall = new P.PhoneAcceptCallRequest
                    {
                        Peer = reference,
                        GB = ByteString.CopyFrom(material.PublicValue),
                        Protocol = BuildProtocol(),
                    },
                }, ct).ConfigureAwait(false);
                accepted = acceptResponse.AcceptCall;
            }
            catch (BackendException ex) when (ex.Retryable)
            {
                // Material is durable; a retry resubmits the same g_b.
                await _calls.SetAcceptStageAsync(call.CallId, AcceptStages.Submitted, ct).ConfigureAwait(false);
                throw new MilkyActionException(MilkyErrorCode.Transient, ex.Code);
            }

            await _calls.SaveSecretAsync(call.CallId, material.Secret.Cipher, material.Secret.Nonce,
                material.Secret.Tag, ct).ConfigureAwait(false);
            await _calls.SetAcceptStageAsync(call.CallId, AcceptStages.Accepted, ct).ConfigureAwait(false);
            if (accepted?.PhoneCall is { } acceptedCall)
                await _calls.SaveSnapshotAsync(call.CallId, acceptedCall.ToByteArray(), ct).ConfigureAwait(false);

            var updated = await TransitionAsync(context, call, CallStates.Negotiating, null, ct)
                .ConfigureAwait(false);
            return ToInfo(updated);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Caller-side confirmation: derives the shared key from the callee's g_b, verifies the
    /// fingerprint and only then tells the backend the call is active.
    /// </summary>
    private async Task<P.PhoneCall?> ConfirmAsync(
        SessionContext context, CallRecord call, P.PhoneCall backendCall, CancellationToken ct)
    {
        if (call.BackendCallId is not { } backendCallId)
            throw new MilkyActionException(MilkyErrorCode.Conflict, "call has no backend identity");

        var dh = await RequireDhConfigAsync(context, ct).ConfigureAwait(false);
        var prime = dh.Value.Config.P.ToByteArray();
        var gb = backendCall.GB?.ToByteArray();
        DhValidation.ValidatePublicValue(gb, prime, "CALL_GB");

        // The caller owns the fingerprint: it derives the key from g_b and commits the value
        // in confirm_call. There is nothing to compare against yet, so a zero fingerprint in
        // the accept update is expected rather than an error.
        var secret = await GetSecretAsync(call, ct).ConfigureAwait(false);
        var key = _dh.SharedSecret(prime, gb!, secret);
        var fingerprint = _dh.Fingerprint(key);
        if (fingerprint == 0)
            throw new DhValidationException("CALL_FINGERPRINT_INVALID",
                "derived call key fingerprint is not a usable value");

        var ga = _dh.PublicValue(prime, dh.Value.Config.G, secret);

        var response = await _rpc.CallAsync(context, new P.RpcRequest
        {
            ConfirmCall = new P.PhoneConfirmCallRequest
            {
                Peer = new P.PhoneCallRef { CallId = backendCallId, AccessHash = call.AccessHash ?? 0 },
                GA = ByteString.CopyFrom(ga),
                KeyFingerprint = fingerprint,
                Protocol = BuildProtocol(),
            },
        }, ct).ConfigureAwait(false);

        await _calls.MarkKeyVerifiedAsync(call.CallId, ct).ConfigureAwait(false);
        _verifiedKeys[call.CallId] = key;
        // The backend emits the active update only to the callee; the caller must use the
        // confirm response, which also carries the negotiated media endpoints.
        var confirmed = response.ConfirmCall?.PhoneCall;
        _logger.LogInformation(
            "confirm response for {CallId}: present={Present} state={State} fingerprint={Fp} gb={Gb} connections={Connections}",
            call.CallId, confirmed is not null, confirmed?.State, confirmed?.KeyFingerprint,
            confirmed?.GB.Length, confirmed?.Connections.Count);
        return confirmed;
    }

    public async Task<CallInfo> EndAsync(SessionContext context, EndCallInput input, CancellationToken ct)
    {
        var call = await RequireAsync(context, input.CallId, ct).ConfigureAwait(false);
        if (call.IsTerminal) return ToInfo(call);

        _ = MapReason(input.Reason); // validate the reason before touching the backend

        if (call.BackendCallId is not { } backendCallId)
        {
            var endedLocally = await TransitionAsync(context, call, CallStates.Ended, input.Reason, ct)
                .ConfigureAwait(false);
            await ReleaseMediaAsync(context, endedLocally, "call_ended", ct).ConfigureAwait(false);
            return ToInfo(endedLocally);
        }

        try
        {
            await DiscardAsync(context, call, backendCallId, input.Reason, ct).ConfigureAwait(false);
        }
        catch (BackendException ex) when (ex.HttpStatus is 400 or 404)
        {
            // The backend already has no such call: ending locally matches reality.
            _logger.LogInformation("call {CallId} already gone on the backend: {Code}", call.CallId, ex.Code);
        }
        catch (BackendException ex)
        {
            // Never claim the line is released when the backend did not confirm it. Local media
            // is closed immediately; the line stays reconcilable in the background.
            await _calls.SetPendingEndAsync(call.CallId, input.Reason, ct).ConfigureAwait(false);
            await ReleaseMediaAsync(context, call, "hangup_pending", ct).ConfigureAwait(false);
            await _calls.MarkReconcilingAsync(call.CallId, ex.Code, Backoff(1), ct).ConfigureAwait(false);
            _logger.LogWarning("discard_call for {CallId} not confirmed: {Code}", call.CallId, ex.Code);
            throw new MilkyActionException(MilkyErrorCode.SendOutcomeUnknown,
                $"hangup outcome unknown ({ex.Code}); media released locally, the line stays reconcilable");
        }

        var ended = await TransitionAsync(context, call, CallStates.Ended, input.Reason, ct).ConfigureAwait(false);
        await ReleaseMediaAsync(context, ended, "call_ended", ct).ConfigureAwait(false);
        return ToInfo(ended);
    }

    public async Task<CallInfo> GetAsync(SessionContext context, string callId, CancellationToken ct)
        => ToInfo(await RequireAsync(context, callId, ct).ConfigureAwait(false));

    public async Task<OpenCallMediaOutput> OpenMediaAsync(
        SessionContext context, OpenCallMediaInput input, CancellationToken ct)
    {
        var call = await RequireAsync(context, input.CallId, ct).ConfigureAwait(false);
        if (call.IsTerminal)
            throw new MilkyActionException(MilkyErrorCode.Conflict, "call already finished");
        if (!input.Input && !input.Output)
            throw new MilkyActionException(MilkyErrorCode.BadRequest, "at least one of input/output must be true");
        if (call.MediaState != "ready")
            throw new MilkyActionException(MilkyErrorCode.MediaUnavailable,
                call.MediaReason ?? "media is not ready for this call");

        var token = "cm_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.Add(_options.MediaTokenLifetime);
        await _calls.CreateMediaTokenAsync(
            context.Account.AccountId, call.CallId, CallStore.HashToken(token), expires, ct).ConfigureAwait(false);

        return new OpenCallMediaOutput
        {
            CallId = call.CallId.ToString(),
            UrlPath = "/nyarugram/calls/" + call.CallId + "/media",
            Token = token,
            ExpiresAt = expires.ToUnixTimeSeconds(),
        };
    }

    // ------------------------------------------------------------- reconciliation

    /// <summary>
    /// Startup pass: calls owned by another process instance cannot have a live media engine
    /// here, so they are ended against the backend. A failed discard keeps the call pending
    /// instead of silently dropping the line.
    /// </summary>
    public async Task ReconcileAsync(SessionContext context, CancellationToken ct)
    {
        await StopStaleSignalingAsync(context).ConfigureAwait(false);
        var foreign = await _calls.ListForeignInstanceAsync(context.Account.AccountId, _runtime.InstanceId, ct)
            .ConfigureAwait(false);
        foreach (var call in foreign)
        {
            if (!_runtime.IsCurrent(context)) return;
            await ReconcileOneAsync(context, call, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Periodic pass: pending releases, hangups and failed media starts.</summary>
    public async Task MaintainAsync(SessionContext context, CancellationToken ct)
    {
        // A login change makes every queue of the previous generation useless.
        await StopStaleSignalingAsync(context).ConfigureAwait(false);
        await ProcessPendingFaultsAsync(ct).ConfigureAwait(false);

        // Any call whose release is still pending — a hangup the backend never confirmed, a
        // key failure that left the line busy, or a startup cleanup of a foreign instance that
        // failed on its first attempt — is retried here until the backend confirms.
        var pendingReconcile = await _calls.ListPendingReconcileAsync(context.Account.AccountId, ct)
            .ConfigureAwait(false);
        foreach (var call in pendingReconcile)
        {
            if (!_runtime.IsCurrent(context)) return;
            await ReconcileOneAsync(context, call, ct).ConfigureAwait(false);
        }

        var media = await _calls.ListMediaRecoveryAsync(context.Account.AccountId, ct).ConfigureAwait(false);
        foreach (var call in media)
        {
            if (!_runtime.IsCurrent(context)) return;
            var gate = LockFor(call.CallId);
            if (!await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) continue;
            try
            {
                var current = await _calls.GetAsync(context.Account.AccountId, call.CallId, ct).ConfigureAwait(false);
                if (current is null || current.IsTerminal) continue;
                if (current.State != CallStates.Active) continue;
                var snapshot = await LoadSnapshotAsync(current, ct).ConfigureAwait(false);
                if (snapshot is null) continue;
                await EnsureMediaAsync(context, current, snapshot, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task ReconcileOneAsync(SessionContext context, CallRecord call, CancellationToken ct)
    {
        var gate = LockFor(call.CallId);
        if (!await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) return;
        try
        {
            var current = await _calls.GetAsync(context.Account.AccountId, call.CallId, ct).ConfigureAwait(false);
            if (current is null) return;
            // A terminal call with no pending release marker needs nothing. A terminal call
            // that still has one (e.g. a key failure that left the backend line busy) must be
            // released without resurrecting its terminal state. The reconciling marker left
            // by a crash between the failed commit and discard is equally actionable.
            if (current.IsTerminal && current.PendingEndReason is null
                && current.ReconcileState != "pending") return;

            _verifiedKeys.TryRemove(current.CallId, out _);
            await StopSignalingAsync(current.CallId).ConfigureAwait(false);
            await DisposeEngineSessionAsync(current.CallId).ConfigureAwait(false);
            await _calls.RevokeMediaTokensAsync(current.CallId, ct).ConfigureAwait(false);

            var reason = current.PendingEndReason ?? current.EndReason ?? "restart";

            if (current.BackendCallId is not { } backendCallId)
            {
                if (current.IsTerminal)
                {
                    await _calls.ClearPendingEndAsync(current.CallId, ct).ConfigureAwait(false);
                    return;
                }
                var closed = await TransitionAsync(context, current, CallStates.Ended, reason, ct)
                    .ConfigureAwait(false);
                await ReleaseMediaAsync(context, closed, reason, ct).ConfigureAwait(false);
                await _calls.ClearReconcilingAsync(current.CallId, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                await DiscardAsync(context, current, backendCallId, reason, ct).ConfigureAwait(false);
                if (current.IsTerminal)
                {
                    // Keep the terminal state and end reason; only clear the busy markers.
                    await ReleaseMediaAsync(context, current, reason, ct).ConfigureAwait(false);
                    await _calls.ClearPendingEndAsync(current.CallId, ct).ConfigureAwait(false);
                    await _calls.ClearReconcilingAsync(current.CallId, ct).ConfigureAwait(false);
                    _logger.LogInformation("released failed call {CallId} line ({Reason})", current.CallId, reason);
                    return;
                }
                var ended = await TransitionAsync(context, current, CallStates.Ended, reason, ct).ConfigureAwait(false);
                await ReleaseMediaAsync(context, ended, reason, ct).ConfigureAwait(false);
                await _calls.ClearReconcilingAsync(current.CallId, ct).ConfigureAwait(false);
                _logger.LogInformation("released call {CallId} ({Reason})", current.CallId, reason);
            }
            catch (BackendException ex) when (ex.HttpStatus is 400 or 404)
            {
                if (current.IsTerminal)
                {
                    await _calls.ClearPendingEndAsync(current.CallId, ct).ConfigureAwait(false);
                    await _calls.ClearReconcilingAsync(current.CallId, ct).ConfigureAwait(false);
                    return;
                }
                var ended = await TransitionAsync(context, current, CallStates.Ended, reason, ct).ConfigureAwait(false);
                await ReleaseMediaAsync(context, ended, reason, ct).ConfigureAwait(false);
                await _calls.ClearReconcilingAsync(current.CallId, ct).ConfigureAwait(false);
            }
            catch (BackendException ex) when (ex.HttpStatus is 403 or 401)
            {
                // The account lost permission to release this call. Keep the projection open
                // with a visible failure instead of pretending the line is free.
                await _calls.MarkReconcilingAsync(current.CallId, ex.Code, Backoff(current.ReconcileAttempts + 1), ct)
                    .ConfigureAwait(false);
                _logger.LogWarning("call {CallId} cannot be released: {Code}", current.CallId, ex.Code);
            }
            catch (BackendException ex)
            {
                await _calls.MarkReconcilingAsync(current.CallId, ex.Code, Backoff(current.ReconcileAttempts + 1), ct)
                    .ConfigureAwait(false);
                _logger.LogDebug("call {CallId} still pending release: {Code}", current.CallId, ex.Code);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DiscardAsync(
        SessionContext context, CallRecord call, long backendCallId, string reason, CancellationToken ct)
    {
        var duration = call.ConnectedAt is { } connected
            ? (int)Math.Max(0, (DateTimeOffset.UtcNow - connected).TotalSeconds)
            : 0;
        await _rpc.CallAsync(context, new P.RpcRequest
        {
            DiscardCall = new P.PhoneDiscardCallRequest
            {
                Peer = new P.PhoneCallRef { CallId = backendCallId, AccessHash = call.AccessHash ?? 0 },
                Duration = duration,
                Reason = MapReasonSafe(reason),
                Video = call.Video,
            },
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------- updates

    public async Task ApplyAsync(SessionContext context, P.PhoneCall call, CancellationToken ct)
    {
        var existing = await _calls.GetByBackendAsync(context.Account.AccountId, call.CallId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            if (!IsIncomingForMe(context, call) || call.State == P.PhoneCallState.Discarded)
            {
                _logger.LogDebug("ignoring call {CallId} that is not addressed to this account", call.CallId);
                return;
            }

            byte[]? commitment = null;
            if (call.GAHash is { Length: > 0 })
            {
                try
                {
                    DhValidation.ValidateHash(call.GAHash.ToByteArray(), "CALL_GA_HASH");
                    commitment = call.GAHash.ToByteArray();
                }
                catch (DhValidationException ex)
                {
                    _logger.LogError("incoming call {CallId} has an invalid g_a_hash: {Code}", call.CallId, ex.Code);
                    return;
                }
            }

            var peerUin = call.AdminTlUserId == context.Account.Uin
                ? call.ParticipantTlUserId
                : call.AdminTlUserId;
            var created = await _calls.CreateWithEventAsync(
                context.Account.AccountId, context.Generation, _runtime.InstanceId, "incoming",
                peerUin, call.Video, CallStates.Incoming,
                record => BuildEvent(context, NyarugramEventType.CallIncoming, record, null),
                ct, commitment).ConfigureAwait(false);
            existing = await _calls.AttachBackendAsync(
                created.CallId, call.CallId, call.AccessHash, _runtime.InstanceId, ct).ConfigureAwait(false);

            try
            {
                await _rpc.CallAsync(context, new P.RpcRequest
                {
                    ReceivedCall = new P.PhoneReceivedCallRequest
                    {
                        Peer = new P.PhoneCallRef { CallId = call.CallId, AccessHash = call.AccessHash },
                    },
                }, ct).ConfigureAwait(false);
            }
            catch (BackendException ex)
            {
                _logger.LogWarning("received_call for {CallId} failed: {Code}", call.CallId, ex.Code);
            }
        }

        var gate = LockFor(existing.CallId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await _calls.GetAsync(context.Account.AccountId, existing.CallId, ct).ConfigureAwait(false);
            if (current is null || current.IsTerminal) return;

            await _calls.SaveSnapshotAsync(current.CallId, call.ToByteArray(), ct).ConfigureAwait(false);

            var target = MapState(call);
            if (target is null) return;

            if (CallStates.IsTerminal(target))
            {
                var reason = MapDiscardReason(call.DiscardReason);
                var ended = await TransitionAsync(context, current, target, reason, ct).ConfigureAwait(false);
                await ReleaseMediaAsync(context, ended, "call_finished", ct).ConfigureAwait(false);
                return;
            }

            if (Capability.RingOnly && current.Direction == "outgoing"
                && Rank(target) >= Rank(CallStates.Negotiating))
            {
                await EndAsync(context, new EndCallInput
                {
                    CallId = current.CallId.ToString(), Reason = "hangup",
                }, ct).ConfigureAwait(false);
                return;
            }

            P.PhoneCall mediaCall = call;
            if (Rank(target) > Rank(current.State))
            {
                // The caller must confirm the key agreement before the call can become active.
                if (current.Direction == "outgoing"
                    && Rank(target) >= Rank(CallStates.Negotiating)
                    && current.KeyState != KeyStates.Verified)
                {
                    try
                    {
                        var confirmed = await ConfirmAsync(context, current, call, ct).ConfigureAwait(false);
                        if (confirmed is not null)
                        {
                            mediaCall = confirmed;
                            // Persist the confirmed call so a later media retry uses the active
                            // snapshot with its key fingerprint and media endpoints.
                            await _calls.SaveSnapshotAsync(current.CallId, confirmed.ToByteArray(), ct)
                                .ConfigureAwait(false);
                            if (MapState(confirmed) == CallStates.Active)
                                target = CallStates.Active;
                        }
                    }
                    catch (DhValidationException ex)
                    {
                        await FailCallAsync(context, current, ex.Code, ct).ConfigureAwait(false);
                        return;
                    }
                    catch (MilkyActionException ex) when (ex.Code == MilkyErrorCode.Conflict)
                    {
                        await FailCallAsync(context, current, "CALL_PROTOCOL_REJECTED", ct).ConfigureAwait(false);
                        return;
                    }
                    catch (BackendException ex)
                    {
                        // Transient: retry on the next difference tick with the same g_a.
                        throw new MilkyActionException(MilkyErrorCode.Transient, ex.Code);
                    }
                }

                current = await TransitionAsync(context, current, target, null, ct).ConfigureAwait(false);
            }

            // Media is recovered independently from the signalling rank: a replayed active
            // update must still retry a start that failed earlier.
            if (current.State == CallStates.Active || current.MediaStage == MediaStages.Pending)
            {
                if (current.MediaStage != MediaStages.Ready)
                    await EnsureMediaAsync(context, current, mediaCall, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ApplySignalingAsync(SessionContext context, P.PhoneCallSignalingData data, CancellationToken ct)
    {
        var call = await _calls.GetByBackendAsync(context.Account.AccountId, data.CallId, ct).ConfigureAwait(false);
        if (call is null || call.IsTerminal) return;
        await _engine.OnSignalingAsync(call.CallId, data.Data.ToByteArray(), ct).ConfigureAwait(false);
    }

    // --------------------------------------------------------------------- media

    private async Task EnsureMediaAsync(
        SessionContext context, CallRecord call, P.PhoneCall backendCall, CancellationToken ct)
    {
        if (call.MediaStage is MediaStages.Ready or MediaStages.Failed) return;
        if (call.State != CallStates.Active) return;
        // Media negotiation needs the peer's signaling, which is delivered by this same
        // synchronization loop. Waiting for the engine inline would deadlock the loop, so
        // the readiness wait runs in the background and only the start is inline.
        if (!_mediaStarting.TryAdd(call.CallId, 0)) return;
        var backgroundStarted = false;

        try
        {
        // A media retry replaces the previous instance; its queue must stop before the new
        // one is registered, otherwise a late signal from the old agent could be delivered.
        await StopSignalingAsync(call.CallId).ConfigureAwait(false);

        if (!_engine.Capability.Available)
        {
            await SetMediaAsync(context, call, "closed", MediaStages.Failed, _engine.Capability.Reason,
                NyarugramEventType.CallMediaClosed, ct).ConfigureAwait(false);
            return;
        }

        byte[] key;
        try
        {
            key = await DeriveAndVerifyKeyAsync(context, call, backendCall, ct).ConfigureAwait(false);
        }
        catch (DhValidationException ex)
        {
            await FailCallAsync(context, call, ex.Code, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var config = await _rpc.CallAsync(context, new P.RpcRequest
            {
                GetCallConfig = new P.PhoneGetCallConfigRequest(),
            }, ct).ConfigureAwait(false);

            var endpoints = backendCall.Connections
                .Select(connection => new MediaEndpoint(
                    connection.Id, connection.Ip, connection.Ipv6, connection.Port,
                    connection.Turn, connection.Stun, connection.Username, connection.Password))
                .ToList();

            var request = new MediaSessionRequest(
                call.CallId, call.PeerUin, call.Video, call.Direction == "outgoing", key,
                endpoints, config.GetCallConfig?.Data ?? "", backendCall.P2PAllowed,
                backendCall.Protocol?.LibraryVersions.ToList());

            var binding = BeginSignaling(context, call, call.BackendCallId ?? backendCall.CallId);
            var session = await _engine.StartAsync(
                request, payload => EnqueueSignal(binding, payload), ct).ConfigureAwait(false);
            binding.Session = session;

            // Re-check after the await: the call may have ended while the engine was starting.
            var afterStart = await _calls.GetAsync(context.Account.AccountId, call.CallId, ct).ConfigureAwait(false);
            if (afterStart is null || afterStart.IsTerminal)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            backgroundStarted = true;
            _ = Task.Run(() => CompleteMediaReadyAsync(context, call, session));
        }
        catch (MilkyActionException ex) when (ex.Code is MilkyErrorCode.Transient or MilkyErrorCode.MediaUnavailable)
        {
            // An engine process that failed to start or died is retryable; the capability
            // check above already handled the "no engine configured at all" case.
            await ScheduleMediaRetryAsync(context, call, ex.Message, ct).ConfigureAwait(false);
        }
        catch (BackendException ex)
        {
            if (ex.Retryable || ex.HttpStatus is 0 or >= 500)
                await ScheduleMediaRetryAsync(context, call, ex.Code, ct).ConfigureAwait(false);
            else
                await FailCallAsync(context, call, ex.Code, ct).ConfigureAwait(false);
        }
        }
        finally
        {
            if (!backgroundStarted)
            {
                _mediaStarting.TryRemove(call.CallId, out _);
                await StopSignalingAsync(call.CallId).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Waits for the engine to become ready without blocking the sync loop that delivers
    /// the peer's signaling. The engine's media-ready event is only written after this.
    /// </summary>
    private async Task CompleteMediaReadyAsync(SessionContext context, CallRecord call, IMediaSession session)
    {
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(_engine.Capability.ReadyOnStart ? 1 : 50));
            await session.WaitForReadyAsync(timeout.Token).ConfigureAwait(false);
            if (!_runtime.IsCurrent(context))
            {
                // The binding changed while media was starting; nothing may outlive it.
                await session.DisposeAsync().ConfigureAwait(false);
                await StopSignalingAsync(call.CallId).ConfigureAwait(false);
                return;
            }

            var ready = await SetMediaAsync(context, call, "ready", MediaStages.Ready, null,
                NyarugramEventType.CallMediaReady, CancellationToken.None).ConfigureAwait(false);
            if (ready is null)
            {
                // The call ended between the readiness check and the write.
                await session.DisposeAsync().ConfigureAwait(false);
                await StopSignalingAsync(call.CallId).ConfigureAwait(false);
                return;
            }

            // A session that fails after readiness cannot be recovered in place: the process
            // is gone, so the call is ended and media_closed is written. The watcher is bound
            // to this exact instance and ignores late faults from a replaced one.
            StartMediaWatcher(context, call.CallId, session);
        }
        catch (OperationCanceledException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await StopSignalingAsync(call.CallId).ConfigureAwait(false);
            await RetryMediaAsync(context, call, "media engine did not become ready in time").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MilkyActionException or IOException
            or System.Net.Sockets.SocketException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await StopSignalingAsync(call.CallId).ConfigureAwait(false);
            await RetryMediaAsync(context, call, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _mediaStarting.TryRemove(call.CallId, out _);
        }
    }

    private async Task RetryMediaAsync(SessionContext context, CallRecord call, string reason)
    {
        try
        {
            if (!_runtime.IsCurrent(context)) return;
            var current = await _calls.GetAsync(context.Account.AccountId, call.CallId, CancellationToken.None)
                .ConfigureAwait(false);
            if (current is null || current.IsTerminal) return;
            await ScheduleMediaRetryAsync(context, current, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MilkyActionException)
        {
        }
        catch (Npgsql.NpgsqlException)
        {
        }
    }

    private async Task ScheduleMediaRetryAsync(
        SessionContext context, CallRecord call, string error, CancellationToken ct)
    {
        var attempts = call.MediaAttempts + 1;
        if (attempts > _options.MediaMaxAttempts)
        {
            _logger.LogError("media start for call {CallId} exhausted {Attempts} attempts: {Error}",
                call.CallId, attempts, error);
            await SetMediaAsync(context, call, "closed", MediaStages.Failed, error,
                NyarugramEventType.CallMediaClosed, ct).ConfigureAwait(false);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempts, 6))));
        await _calls.ScheduleMediaRetryAsync(call.CallId, error, DateTimeOffset.UtcNow.Add(delay), ct)
            .ConfigureAwait(false);
        _logger.LogWarning("media start for call {CallId} failed ({Error}); retry {Attempt} in {Delay}",
            call.CallId, error, attempts, delay);
    }

    /// <summary>
    /// Derives the shared key and verifies every protocol requirement before it may be used:
    /// public value range, the caller's original g_a commitment (callee side) and the final
    /// fingerprint (both sides). A verified key is cached together with its verification.
    /// </summary>
    private async Task<byte[]> DeriveAndVerifyKeyAsync(
        SessionContext context, CallRecord call, P.PhoneCall backendCall, CancellationToken ct)
    {
        if (_verifiedKeys.TryGetValue(call.CallId, out var cached))
        {
            // A cached key is only reused when the current update still carries the same
            // fingerprint. An older snapshot without a fingerprint does not invalidate a
            // key that was already verified; a different fingerprint does.
            if (backendCall.KeyFingerprint == 0 || _dh.Fingerprint(cached) == backendCall.KeyFingerprint)
                return cached;
            _verifiedKeys.TryRemove(call.CallId, out _);
        }

        var dh = await RequireDhConfigAsync(context, ct).ConfigureAwait(false);
        var prime = dh.Value.Config.P.ToByteArray();

        var other = call.Direction == "outgoing"
            ? backendCall.GB?.ToByteArray()
            : backendCall.GAOrB?.ToByteArray();
        DhValidation.ValidatePublicValue(other, prime, call.Direction == "outgoing" ? "CALL_GB" : "CALL_GA");

        if (call.Direction != "outgoing")
        {
            if (call.PeerGaHash is not { Length: > 0 } commitment)
                throw new DhValidationException("CALL_GA_HASH_MISSING",
                    "incoming call did not carry the caller's g_a commitment");
            if (!DhValidation.MatchesCommitment(other!, commitment))
                throw new DhValidationException("CALL_GA_HASH_MISMATCH",
                    "g_a does not match the caller's original commitment");
        }

        var secret = await GetSecretAsync(call, ct).ConfigureAwait(false);
        var key = _dh.SharedSecret(prime, other!, secret);

        if (backendCall.KeyFingerprint == 0)
            throw new DhValidationException("CALL_FINGERPRINT_MISSING",
                "call update carries no key fingerprint");
        if (_dh.Fingerprint(key) != backendCall.KeyFingerprint)
            throw new DhValidationException("CALL_FINGERPRINT_MISMATCH",
                "derived key does not match the negotiated fingerprint");

        await _calls.MarkKeyVerifiedAsync(call.CallId, ct).ConfigureAwait(false);
        _verifiedKeys[call.CallId] = key;
        return key;
    }

    // --------------------------------------------------------- signaling delivery

    /// <summary>
    /// Creates the relay for one media instance. The relay captures the server/account
    /// generation and call, and only delivers while this binding is still the registered
    /// one, so a late signal from a replaced agent is dropped locally.
    /// </summary>
    private SignalingBinding BeginSignaling(SessionContext context, CallRecord call, long backendCallId)
    {
        var binding = new SignalingBinding
        {
            AccountId = context.Account.AccountId,
            Generation = context.Generation,
        };
        var relay = new SignalingRelay(
            (payload, token) => SendSignalingAsync(context, call, backendCallId, payload, token),
            _options,
            reason => OnSignalingFailure(context, call.CallId, binding, reason),
            () => IsSignalingCurrent(context, call.CallId, binding),
            _logger);
        binding.Relay = relay;
        _signals[call.CallId] = binding;
        return binding;
    }

    /// <summary>
    /// Queues one engine signal without ever blocking the engine's read loop on the
    /// network. Ordering, retries and bounds are owned by <see cref="SignalingRelay"/>.
    /// </summary>
    private static Task EnqueueSignal(SignalingBinding binding, byte[] payload)
    {
        binding.Relay?.Enqueue(payload);
        return Task.CompletedTask;
    }

    private bool IsSignalingCurrent(SessionContext context, Guid callId, SignalingBinding binding)
        => _runtime.IsCurrent(context)
           && _signals.TryGetValue(callId, out var active)
           && ReferenceEquals(active, binding)
           && binding.Relay is not null
           && (binding.Session is null || _engine.Find(callId) == binding.Session);

    private async Task SendSignalingAsync(
        SessionContext context, CallRecord call, long backendCallId, byte[] payload, CancellationToken ct)
    {
        await _rpc.CallAsync(context, new P.RpcRequest
        {
            SendSignalingData = new P.PhoneSendSignalingDataRequest
            {
                Peer = new P.PhoneCallRef { CallId = backendCallId, AccessHash = call.AccessHash ?? 0 },
                Data = ByteString.CopyFrom(payload),
            },
        }, ct).ConfigureAwait(false);
    }

    private void OnSignalingFailure(
        SessionContext context, Guid callId, SignalingBinding binding, string reason)
    {
        _logger.LogError("signaling for call {CallId} was abandoned ({Reason})", callId, reason);
        _ = Task.Run(() => HandleSignalingFailureAsync(context, callId, binding, reason));
    }

    /// <summary>
    /// A relay that overflowed, expired or exhausted its retries can no longer keep the
    /// negotiation consistent. The call is failed and released through the normal path so
    /// the peer is not left with a half-negotiated line. The callback carries the exact
    /// binding that failed: after taking the call lock the binding must still be the
    /// registered one, otherwise a replaced media instance must not be closed.
    /// </summary>
    private async Task HandleSignalingFailureAsync(
        SessionContext context, Guid callId, SignalingBinding binding, string reason)
    {
        try
        {
            if (BeforeSignalingFailureLockForTests is { } delayForTests)
                await delayForTests(callId).ConfigureAwait(false);

            var gate = LockFor(callId);
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!IsContextCurrent(context))
                {
                    _logger.LogDebug("dropping signaling failure for {CallId} from an old session", callId);
                    return;
                }
                if (!IsBindingCurrent(callId, binding))
                {
                    _logger.LogDebug("dropping signaling failure for {CallId} from a replaced binding", callId);
                    return;
                }

                await StopSignalingAsync(callId, binding).ConfigureAwait(false);
                await StopBindingMediaAsync(callId, binding).ConfigureAwait(false);
                await FinalizeOrScheduleFaultAsync(
                    context, callId, reason, null, binding, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "signaling failure handling for call {CallId} failed", callId);
        }
    }

    /// <summary>
    /// True when this binding is still the registered one. A binding created before
    /// StartAsync returned has no session yet; that startup phase is still current.
    /// </summary>
    private bool IsBindingCurrent(Guid callId, SignalingBinding binding)
    {
        if (!_signals.TryGetValue(callId, out var active) || !ReferenceEquals(active, binding))
            return false;
        if (binding.Session is not null && _engine.Find(callId) != binding.Session)
            return false;
        return true;
    }

    private async Task StopSignalingAsync(Guid callId, SignalingBinding? expected = null)
    {
        if (expected is null)
        {
            if (_signals.TryRemove(callId, out var binding))
                await DisposeRelayAsync(binding).ConfigureAwait(false);
            return;
        }

        // Remove only this exact instance: an old callback must never remove the
        // replacement that was installed for the same call id.
        var entries = (ICollection<KeyValuePair<Guid, SignalingBinding>>)_signals;
        if (entries.Remove(new KeyValuePair<Guid, SignalingBinding>(callId, expected)))
            await DisposeRelayAsync(expected).ConfigureAwait(false);
    }

    private static async Task DisposeRelayAsync(SignalingBinding binding)
    {
        if (binding.Relay is null) return;
        try
        {
            await binding.Relay.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The relay is already unusable; disposal is best effort.
        }
    }

    private bool IsContextCurrent(SessionContext context)
        => _runtime.IsCurrent(context)
           && _runtime.Current is { } live
           && string.Equals(live.Account.ServerDomain, context.Account.ServerDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>Drops relays whose account or login generation no longer owns them.</summary>
    private async Task StopStaleSignalingAsync(SessionContext context)
    {
        foreach (var (callId, binding) in _signals)
        {
            if (binding.AccountId == context.Account.AccountId
                && binding.Generation == context.Generation) continue;
            _verifiedKeys.TryRemove(callId, out _);
            await StopSignalingAsync(callId, binding).ConfigureAwait(false);
            await DisposeEngineSessionAsync(callId).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------- media runtime faults

    private void StartMediaWatcher(SessionContext context, Guid callId, IMediaSession session)
        => _ = Task.Run(() => WatchMediaAsync(context, callId, session));

    private async Task WatchMediaAsync(SessionContext context, Guid callId, IMediaSession session)
    {
        MediaFault? fault;
        try
        {
            fault = await session.Completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "media completion for call {CallId} produced no fault", callId);
            return;
        }
        if (fault is null) return; // an intentional close is handled by its owner

        try
        {
            await HandleMediaFaultAsync(context, callId, session, fault).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "media fault handling for call {CallId} failed", callId);
        }
    }

    /// <summary>
    /// Runtime media failure after readiness. The engine instance is already dead and
    /// cannot be recovered in place. Its resources are stopped before any database
    /// access, so a database outage cannot keep the process, socket or audio tasks alive.
    /// The failed/failed-media transaction and line release are then finalized or, if
    /// the database is unavailable, retained in memory and retried with backoff.
    /// </summary>
    private async Task HandleMediaFaultAsync(
        SessionContext context, Guid callId, IMediaSession session, MediaFault fault)
    {
        var gate = LockFor(callId);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!IsContextCurrent(context))
            {
                await DisposeSessionIfCurrentAsync(callId, session).ConfigureAwait(false);
                return;
            }
            if (_engine.Find(callId) != session)
            {
                _logger.LogDebug("ignoring a late media fault for call {CallId} from a replaced instance", callId);
                return;
            }

            _logger.LogError("media for call {CallId} terminated unexpectedly: {Reason} {Detail}",
                callId, fault.Reason, fault.Detail);

            var binding = BindingForSession(callId, session);
            if (binding is not null)
                await StopSignalingAsync(callId, binding).ConfigureAwait(false);
            await DisposeSessionIfCurrentAsync(callId, session).ConfigureAwait(false);

            await FinalizeOrScheduleFaultAsync(
                context, callId, "media_agent_failed", session, binding, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SignalingBinding? BindingForSession(Guid callId, IMediaSession session)
    {
        if (!_signals.TryGetValue(callId, out var binding)) return null;
        return binding.Session == session ? binding : null;
    }

    /// <summary>Stops the media owned by a binding without touching a replacement.</summary>
    private async Task StopBindingMediaAsync(Guid callId, SignalingBinding binding)
    {
        if (binding.Session is null)
        {
            // The failure happened during StartAsync, before the session was assigned
            // to the binding. Stop the engine instance for this call if it exists.
            await DisposeEngineSessionAsync(callId).ConfigureAwait(false);
            return;
        }
        await DisposeSessionIfCurrentAsync(callId, binding.Session).ConfigureAwait(false);
    }

    private async Task DisposeSessionIfCurrentAsync(Guid callId, IMediaSession session)
    {
        if (_engine.Find(callId) != session) return;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "media session for {CallId} did not dispose cleanly", callId);
        }
    }

    private async Task FailCallAsync(
        SessionContext context, CallRecord call, string reason, CancellationToken ct)
    {
        _verifiedKeys.TryRemove(call.CallId, out _);
        try
        {
            await _calls.MarkKeyFailedAsync(call.CallId, reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "could not persist key failure for {CallId}; finalization will retry", call.CallId);
        }
        // Stop local media before any database work: an unavailable database must not
        // leave a process, socket or audio task running for a call that is failing.
        await StopSignalingAsync(call.CallId).ConfigureAwait(false);
        await DisposeEngineSessionAsync(call.CallId).ConfigureAwait(false);
        await FinalizeOrScheduleFaultAsync(context, call.CallId, reason, null, null, ct).ConfigureAwait(false);
        _logger.LogError("call {CallId} failed: {Reason}", call.CallId, reason);
    }

    /// <summary>
    /// Runs the fault finalization or retains it for the background finalizer when the
    /// database is unavailable. The media and signaling resources were already stopped
    /// by the caller, so a pending entry only contains persistence work.
    /// </summary>
    private async Task FinalizeOrScheduleFaultAsync(
        SessionContext context, Guid callId, string reason, IMediaSession? media,
        SignalingBinding? binding, CancellationToken ct)
    {
        try
        {
            await FinalizeFailureAsync(context, callId, reason, media, binding, ct).ConfigureAwait(false);
            _pendingFaults.TryRemove(callId, out _);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SchedulePendingFault(context, callId, reason, media, binding, ex);
        }
    }

    private void SchedulePendingFault(
        SessionContext context, Guid callId, string reason, IMediaSession? media,
        SignalingBinding? binding, Exception error)
    {
        var pending = new PendingFault
        {
            Context = context,
            CallId = callId,
            Reason = reason,
            Media = media,
            Binding = binding,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        // The caller holds the per-call lock and has just verified the instance, so
        // replacing a previous entry for the same call cannot clobber a newer media.
        _pendingFaults[callId] = pending;
        _logger.LogWarning(error,
            "fault finalization for call {CallId} deferred; will retry ({Reason})", callId, reason);
    }

    /// <summary>
    /// Idempotent, resumable fault finalization. It is safe to call repeatedly after a
    /// partial success: the reconciling marker is written first, the failed transition
    /// is version-guarded, media_closed is emitted only by the transition that applies,
    /// token revocation is a delete, and discard is confirmed before markers are cleared.
    /// </summary>
    private async Task FinalizeFailureAsync(
        SessionContext context, Guid callId, string reason, IMediaSession? media,
        SignalingBinding? binding, CancellationToken ct)
    {
        _verifiedKeys.TryRemove(callId, out _);
        var call = await _calls.GetAsync(context.Account.AccountId, callId, ct).ConfigureAwait(false);
        if (call is null) return;

        // A concurrent hangup already reached a terminal state. Still revoke any media
        // token this instance might have issued; no second terminal event is written.
        if (call.IsTerminal && call.State == CallStates.Ended)
        {
            CheckFinalizationFault("revoke_tokens");
            await _calls.RevokeMediaTokensAsync(callId, ct).ConfigureAwait(false);
            return;
        }

        // Persist the release intent before the terminal transition. A crash after the
        // failed commit but before discard leaves this marker for startup reconciliation.
        CheckFinalizationFault("mark_reconciling");
        await _calls.MarkReconcilingAsync(callId, "failure_finalize", Backoff(1), ct).ConfigureAwait(false);

        CheckFinalizationFault("transition");
        var failed = await TransitionAsync(context, call, CallStates.Failed, reason, ct).ConfigureAwait(false);

        // Terminal state and media_closed are now committed (or another path won).
        // Revocation may still fail; a retry resumes from this committed state.
        CheckFinalizationFault("revoke_tokens");
        await _calls.RevokeMediaTokensAsync(callId, ct).ConfigureAwait(false);

        if (failed.State != CallStates.Failed)
        {
            // Another terminal decision won; release only this instance's resources.
            await ReleaseMediaAsync(context, failed, reason, ct, media, binding).ConfigureAwait(false);
            return;
        }

        CheckFinalizationFault("release_media");
        await ReleaseMediaAsync(context, failed, reason, ct, media, binding).ConfigureAwait(false);

        if (failed.BackendCallId is not { } backendCallId) return;

        try
        {
            CheckFinalizationFault("discard");
            await DiscardAsync(context, failed, backendCallId, "failed", ct).ConfigureAwait(false);
        }
        catch (BackendException ex) when (ex.HttpStatus is 400 or 404)
        {
            // Already gone on the backend; the line is released.
        }
        catch (BackendException ex)
        {
            await _calls.SetPendingEndAsync(failed.CallId, "failed", ct).ConfigureAwait(false);
            await _calls.MarkReconcilingAsync(failed.CallId, ex.Code, Backoff(1), ct).ConfigureAwait(false);
            _logger.LogWarning("failed call {CallId} line release deferred: {Code}", failed.CallId, ex.Code);
            return;
        }

        // The backend line is released: clear both the pending-end and reconciling markers.
        await _calls.ClearPendingEndAsync(callId, ct).ConfigureAwait(false);
        await _calls.ClearReconcilingAsync(callId, ct).ConfigureAwait(false);
    }

    private void CheckFinalizationFault(string step)
    {
        if (FinalizationFaultForTests?.Invoke(step) is { } error) throw error;
    }

    /// <summary>Retries pending fault finalizations with bounded exponential backoff.</summary>
    internal async Task ProcessPendingFaultsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (callId, pending) in _pendingFaults)
        {
            ct.ThrowIfCancellationRequested();
            if (pending.NextAttemptAt > now) continue;

            var gate = LockFor(callId);
            if (!await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) continue;
            try
            {
                if (!_pendingFaults.TryGetValue(callId, out var current) || !ReferenceEquals(current, pending))
                    continue;
                // A login change invalidates credentials, not the unfinished cleanup.
                // Retain the intent while logged out or bound to another account/server.
                // The same owner may resume it with its current authenticated session.
                var context = _runtime.Current;
                if (context is null
                    || context.Account.AccountId != pending.Context.Account.AccountId
                    || context.Account.AccountUuid != pending.Context.Account.AccountUuid
                    || !string.Equals(context.Account.ServerDomain, pending.Context.Account.ServerDomain,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsPendingInstanceReusable(pending))
                {
                    _pendingFaults.TryRemove(callId, out _);
                    _logger.LogDebug("dropping pending fault for {CallId}: media or signaling instance was replaced", callId);
                    continue;
                }

                try
                {
                    await FinalizeFailureAsync(
                        context, callId, pending.Reason, pending.Media, pending.Binding, ct)
                        .ConfigureAwait(false);
                    _pendingFaults.TryRemove(callId, out _);
                    _logger.LogInformation("pending fault finalization completed for call {CallId}", callId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    pending.Attempts++;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(pending.Attempts, 5))));
                    pending.NextAttemptAt = DateTimeOffset.UtcNow + delay;
                    _logger.LogWarning(ex,
                        "pending fault finalization for call {CallId} failed (attempt {Attempt}); retry in {Delay}",
                        callId, pending.Attempts, delay);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// A pending entry may only act while no newer binding/media instance is registered.
    /// Absence of both is allowed: the fault handler stops its resources before scheduling.
    /// </summary>
    private bool IsPendingInstanceReusable(PendingFault pending)
    {
        if (pending.Binding is not null)
        {
            if (_signals.TryGetValue(pending.CallId, out var active) && !ReferenceEquals(active, pending.Binding))
                return false;
            if (pending.Binding.Session is not null)
            {
                var found = _engine.Find(pending.CallId);
                if (found is not null && found != pending.Binding.Session) return false;
            }
        }
        if (pending.Media is not null)
        {
            var found = _engine.Find(pending.CallId);
            if (found is not null && found != pending.Media) return false;
        }
        return true;
    }

    private async Task ReleaseMediaAsync(
        SessionContext context, CallRecord call, string reason, CancellationToken ct,
        IMediaSession? expectedSession = null, SignalingBinding? expectedBinding = null)
    {
        _verifiedKeys.TryRemove(call.CallId, out _);
        if (expectedBinding is not null)
            await StopSignalingAsync(call.CallId, expectedBinding).ConfigureAwait(false);
        else
            await StopSignalingAsync(call.CallId).ConfigureAwait(false);
        if (expectedSession is not null)
            await DisposeSessionIfCurrentAsync(call.CallId, expectedSession).ConfigureAwait(false);
        else
            await DisposeEngineSessionAsync(call.CallId).ConfigureAwait(false);
        await _calls.RevokeMediaTokensAsync(call.CallId, ct).ConfigureAwait(false);
        if (call.MediaState != "closed")
            await SetMediaAsync(context, call, "closed", MediaStages.Closed, reason,
                NyarugramEventType.CallMediaClosed, ct).ConfigureAwait(false);
    }

    private async Task DisposeEngineSessionAsync(Guid callId)
    {
        if (_engine.Find(callId) is { } session)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "media session for {CallId} did not dispose cleanly", callId);
            }
        }
    }

    private async Task<CallRecord?> SetMediaAsync(
        SessionContext context, CallRecord call, string mediaState, string mediaStage, string? reason,
        string eventType, CancellationToken ct)
    {
        var (updated, applied) = await _calls.SetMediaAsync(
            context.Account.AccountId, call.CallId, mediaState, mediaStage, reason,
            record => BuildEvent(context, eventType, record, reason), ct).ConfigureAwait(false);
        if (!applied)
        {
            _logger.LogDebug("media state for {CallId} not applied (call closed?)", call.CallId);
            return null;
        }
        return updated;
    }

    // -------------------------------------------------------------------- helpers

    private async Task<(P.DhConfig Config, int Version)?> RequireDhConfigAsync(
        SessionContext context, CancellationToken ct)
    {
        var response = await _rpc.CallAsync(context, new P.RpcRequest
        {
            GetDhConfig = new P.GetDhConfigRequest { Version = 0, RandomLength = 256 },
        }, ct).ConfigureAwait(false);
        var config = response.GetDhConfig
            ?? throw new MilkyActionException(MilkyErrorCode.Transient, "backend returned no DH config");
        try
        {
            DhValidation.ValidatePrime(config.P.ToByteArray(), config.G);
        }
        catch (DhValidationException ex)
        {
            throw new MilkyActionException(MilkyErrorCode.Conflict, ex.Message);
        }
        return (config, config.Version);
    }

    private (byte[] Secret, byte[] PublicHash, CallSecret Protection) CreateKeyMaterial(
        (P.DhConfig Config, int Version)? dh)
    {
        var config = dh!.Value.Config;
        var secret = _dh.RandomSecret(256);
        var publicValue = _dh.PublicValue(config.P.ToByteArray(), config.G, secret);
        DhValidation.ValidatePublicValue(publicValue, config.P.ToByteArray(), "CALL_GA");
        var hash = _dh.Sha256(publicValue);
        var (cipher, nonce, tag) = _protector.Protect(Convert.ToBase64String(secret));
        return (secret, hash, new CallSecret(cipher, nonce, tag));
    }

    private (byte[] Secret, byte[] PublicHash, CallSecret Protection) MaterialFrom(CallOperation operation)
    {
        var secret = Convert.FromBase64String(
            _protector.Unprotect(operation.Secret.Cipher, operation.Secret.Nonce, operation.Secret.Tag));
        return (secret, operation.GaHash, operation.Secret);
    }

    private async Task<byte[]> GetSecretAsync(CallRecord call, CancellationToken ct)
    {
        var stored = await _calls.GetSecretAsync(call.CallId, ct).ConfigureAwait(false);
        if (stored is null)
        {
            // An accept that lost its response persisted the private key to the accept-specific
            // columns but never promoted it to the generic ones. Fall back so the callee can
            // still derive the shared key when the caller's confirm arrives.
            stored = await _calls.GetAcceptSecretAsync(call.CallId, ct).ConfigureAwait(false);
        }
        if (stored is null)
            throw new MilkyActionException(MilkyErrorCode.Conflict, "call secret is not available");
        return Convert.FromBase64String(_protector.Unprotect(stored.Cipher, stored.Nonce, stored.Tag));
    }

    private async Task<P.PhoneCall?> LoadSnapshotAsync(CallRecord call, CancellationToken ct)
    {
        var blob = await _calls.GetSnapshotAsync(call.CallId, ct).ConfigureAwait(false);
        return blob is { Length: > 0 } ? P.PhoneCall.Parser.ParseFrom(blob) : null;
    }

    private async Task<CallRecord?> WaitForCallAsync(
        SessionContext context, string requestId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _options.OperationWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var operation = await _calls.GetOperationAsync(context.Account.AccountId, requestId, ct)
                .ConfigureAwait(false);
            if (operation?.CallId is { } callId)
                return await _calls.GetAsync(context.Account.AccountId, callId, ct).ConfigureAwait(false);
            await Task.Delay(120, ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<CallRecord> RequireAsync(SessionContext context, string callId, CancellationToken ct)
    {
        if (!Guid.TryParse(callId, out var id))
            throw new MilkyActionException(MilkyErrorCode.BadRequest, "call_id must be a UUID");
        var call = await _calls.GetAsync(context.Account.AccountId, id, ct).ConfigureAwait(false);
        return call ?? throw new MilkyActionException(MilkyErrorCode.NotFound, $"call {callId} not found");
    }

    private async Task<CallRecord> TransitionAsync(
        SessionContext context, CallRecord call, string state, string? reason, CancellationToken ct)
    {
        // A terminal transition that was carrying media must close it atomically and emit a
        // media-closed event in the same transaction, so the call can never end observably
        // "media ready" without its close event.
        var closesMedia = state is CallStates.Ended or CallStates.Failed
                          && call.MediaState is not ("closed" or "none");
        var (updated, applied) = await _calls.TransitionWithEventAsync(
            context.Account.AccountId, call.CallId, call.StateVersion, state, reason, null, reason,
            record => BuildEvent(context, NyarugramEventType.CallStateChanged, record, reason), ct,
            closesMedia
                ? record => BuildEvent(context, NyarugramEventType.CallMediaClosed, record, reason)
                : null)
            .ConfigureAwait(false);

        if (!applied)
        {
            var current = await _calls.GetAsync(context.Account.AccountId, call.CallId, ct).ConfigureAwait(false)
                ?? throw new MilkyActionException(MilkyErrorCode.NotFound, "call disappeared");
            return current;
        }
        return updated!;
    }

    /// <summary>
    /// Stable per logical event: the type and the relevant version are part of the id, so a
    /// state change and a media change that share a call state version never collide.
    /// </summary>
    private static (string EventId, string EventType, string Payload) BuildEvent(
        SessionContext context, string eventType, CallRecord call, string? reason)
    {
        var eventId = eventType switch
        {
            NyarugramEventType.CallIncoming => $"call:{call.CallId}:incoming:{call.StateVersion}",
            NyarugramEventType.CallStateChanged => $"call:{call.CallId}:state:{call.StateVersion}",
            NyarugramEventType.CallMediaReady => $"call:{call.CallId}:media:{call.MediaVersion}:ready",
            NyarugramEventType.CallMediaClosed =>
                $"call:{call.CallId}:media:{call.MediaVersion}:closed:{Sanitize(reason)}",
            _ => $"call:{call.CallId}:{eventType}:{call.StateVersion}:{call.MediaVersion}",
        };

        var data = new CallEventData
        {
            EventId = eventId,
            CallId = call.CallId.ToString(),
            Direction = call.Direction,
            PeerId = call.PeerUin,
            State = call.State,
            StateVersion = call.StateVersion,
            Video = call.Video,
            Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Reason = reason,
            Media = call.MediaState == "ready" ? new CallMediaInfo() : null,
        };
        var evt = new MilkyEvent
        {
            Time = data.Time,
            SelfId = context.Account.Uin,
            EventType = eventType,
            Data = data,
        };
        return (eventId, eventType, JsonSerializer.Serialize(evt, MilkyJson.Options));
    }

    private SemaphoreSlim LockFor(Guid callId) => _callLocks.GetOrAdd(callId, _ => new SemaphoreSlim(1, 1));

    private static TimeSpan Backoff(int attempts)
        => TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, Math.Min(attempts, 7))));

    private static string Sanitize(string? reason)
        => string.IsNullOrEmpty(reason)
            ? "unspecified"
            : new string(reason.Take(48).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    private P.PhoneCallProtocol BuildProtocol()
    {
        var capability = _engine.Capability;
        var protocol = new P.PhoneCallProtocol
        {
            UdpP2P = capability.UdpP2P,
            UdpReflector = capability.UdpReflector,
            MinLayer = capability.MinLayer,
            MaxLayer = capability.MaxLayer,
        };
        if (capability.LibraryVersions is { Count: > 0 } versions) protocol.LibraryVersions.AddRange(versions);
        return protocol;
    }

    internal static CallInfo ToInfo(CallRecord call) => new()
    {
        CallId = call.CallId.ToString(),
        Direction = call.Direction,
        PeerId = call.PeerUin,
        State = call.State,
        Video = call.Video,
        CreatedAt = call.CreatedAt.ToUnixTimeSeconds(),
        ConnectedAt = call.ConnectedAt?.ToUnixTimeSeconds(),
        EndedAt = call.EndedAt?.ToUnixTimeSeconds(),
        EndReason = call.EndReason,
        MediaState = call.MediaState,
        MediaReason = call.MediaReason,
        StateVersion = call.StateVersion,
    };

    private static bool IsIncomingForMe(SessionContext context, P.PhoneCall call)
        => call.State is P.PhoneCallState.Waiting or P.PhoneCallState.Requested
           && call.ParticipantTlUserId == context.Account.Uin;

    private static string? MapState(P.PhoneCall call) => call.State switch
    {
        P.PhoneCallState.Waiting => CallStates.Ringing,
        P.PhoneCallState.Requested => CallStates.Ringing,
        P.PhoneCallState.Accepted => CallStates.Negotiating,
        P.PhoneCallState.Active => CallStates.Active,
        P.PhoneCallState.Discarded => CallStates.Ended,
        _ => null,
    };

    private static string? MapDiscardReason(P.PhoneCallDiscardReason reason) => reason switch
    {
        P.PhoneCallDiscardReason.Hangup => "hangup",
        P.PhoneCallDiscardReason.Missed => "missed",
        P.PhoneCallDiscardReason.Busy => "busy",
        P.PhoneCallDiscardReason.Disconnect => "disconnect",
        _ => null,
    };

    /// <summary>Internal reasons (restart, hangup_pending, …) must never fail a discard.</summary>
    private static P.PhoneCallDiscardReason MapReasonSafe(string reason) => reason switch
    {
        "missed" or "timeout" => P.PhoneCallDiscardReason.Missed,
        "busy" => P.PhoneCallDiscardReason.Busy,
        "disconnect" or "failed" or "restart" => P.PhoneCallDiscardReason.Disconnect,
        _ => P.PhoneCallDiscardReason.Hangup,
    };

    private static P.PhoneCallDiscardReason MapReason(string reason) => reason switch
    {
        "hangup" => P.PhoneCallDiscardReason.Hangup,
        "missed" => P.PhoneCallDiscardReason.Missed,
        "busy" => P.PhoneCallDiscardReason.Busy,
        "disconnect" => P.PhoneCallDiscardReason.Disconnect,
        "cancel" => P.PhoneCallDiscardReason.Hangup,
        "timeout" => P.PhoneCallDiscardReason.Missed,
        "failed" => P.PhoneCallDiscardReason.Disconnect,
        _ => throw new MilkyActionException(MilkyErrorCode.BadRequest, $"unknown end reason '{reason}'"),
    };

    private static int Rank(string state) => state switch
    {
        CallStates.Incoming or CallStates.Outgoing => 0,
        CallStates.Ringing => 1,
        CallStates.Accepted => 2,
        CallStates.Negotiating => 3,
        CallStates.Active => 4,
        _ => 5,
    };

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static long NewRandomId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        var value = BitConverter.ToInt64(buffer);
        return value == 0 ? 1 : value;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var callId in _signals.Keys) await StopSignalingAsync(callId).ConfigureAwait(false);
        foreach (var callId in _verifiedKeys.Keys) await DisposeEngineSessionAsync(callId).ConfigureAwait(false);
        _verifiedKeys.Clear();
    }
}


/// <summary>
/// Periodic call maintenance. Runs inside the account session scope: a hangup that the
/// backend never confirmed is retried here until it succeeds, and a media start that failed
/// transiently is retried with bounded backoff. Both stop as soon as the account changes.
/// </summary>
public sealed class CallMaintenanceWorker : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly MilkyRuntime _runtime;
    private readonly CallService _calls;
    private readonly RuntimeOptions _options;
    private readonly ILogger<CallMaintenanceWorker> _logger;
    private bool _startupReconciled;

    public CallMaintenanceWorker(
        MilkyRuntime runtime, CallService calls, RuntimeOptions options, ILogger<CallMaintenanceWorker> logger)
    {
        _runtime = runtime;
        _calls = calls;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.BackgroundCallMaintenance)
        {
            _logger.LogWarning("call maintenance is disabled by configuration");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var context = _runtime.Current;
            if (context is null)
            {
                _startupReconciled = false;
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (!_startupReconciled)
                {
                    await _calls.ReconcileAsync(context, stoppingToken).ConfigureAwait(false);
                    _startupReconciled = true;
                }
                await _calls.MaintainAsync(context, stoppingToken).ConfigureAwait(false);
            }
            catch (MilkyActionException ex) when (ex.Code == MilkyErrorCode.NotLoggedIn)
            {
                _startupReconciled = false;
            }
            catch (BackendException ex)
            {
                _logger.LogDebug("call maintenance deferred: {Code}", ex.Code);
            }
            catch (Npgsql.NpgsqlException ex)
            {
                _logger.LogError(ex, "call maintenance storage error");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "call maintenance pass failed");
            }

            await Task.Delay(_options.ReconcileInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Retries in-memory fault finalizations independently of the call maintenance interval.
/// Entries are bound to the captured session and instance; they are dropped when the
/// account generation or media/signaling instance changes. A process restart does not
/// replay these entries: startup reconciliation ends calls owned by the previous instance.
/// </summary>
public sealed class CallFaultFinalizer : Microsoft.Extensions.Hosting.BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly CallService _calls;
    private readonly ILogger<CallFaultFinalizer> _logger;

    public CallFaultFinalizer(CallService calls, ILogger<CallFaultFinalizer> logger)
    {
        _calls = calls;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _calls.ProcessPendingFaultsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "call fault finalizer pass failed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
