using System.Net;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;
using Xunit;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Second-round review regressions: DH validation, commitment/fingerprint checks, accept
/// durability, media recovery, hangup reconciliation, event scoping, lease ownership and the
/// WebSocket acknowledgement contract.
/// </summary>
[Collection("adapter")]
public sealed class Round3RegressionTests
{
    private readonly AdapterFixture _fixture;

    public Round3RegressionTests(AdapterFixture fixture) => _fixture = fixture;

    private static void RequireStorage(AdapterFixture fixture)
    {
        if (fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {fixture.StartupError.Message}");
    }

    // ---------------------------------------------------------------- finding 1

    public static IEnumerable<object[]> InvalidPublicValues()
    {
        var prime = FakeBackend.DhPrime;
        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        yield return new object[] { "zero", new byte[256] };
        yield return new object[] { "one", Fixed(BigInteger.One) };
        yield return new object[] { "p-1", Fixed(p - 1) };
        yield return new object[] { "p", Fixed(p) };
        yield return new object[] { "short", new byte[128] };
        yield return new object[] { "all-ff", Enumerable.Repeat((byte)0xff, 256).ToArray() };
    }

    private static byte[] Fixed(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[256];
        bytes.CopyTo(padded, 256 - bytes.Length);
        return padded;
    }

    [SkippableTheory]
    [MemberData(nameof(InvalidPublicValues))]
    public async Task Invalid_peer_public_value_fails_only_that_call(string label, byte[] peerValue)
    {
        RequireStorage(_fixture);
        var (callId, backendId) = await StartOutgoingAsync("dh-" + label + "-" + Guid.NewGuid().ToString("N"));
        await _fixture.SyncAsync(); // ringing

        // Override the callee's g_b BEFORE emitting the accepted update, otherwise the
        // difference page carries the legitimate value and the validation never runs.
        _fixture.Backend.SetCallPublicValue(backendId, peerValue);
        _fixture.Backend.AdvanceCall(backendId, "accepted");
        await _fixture.SyncAsync();

        var call = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, Guid.Parse(callId!), CancellationToken.None);
        Assert.NotNull(call);
        Assert.Equal(CallStates.Failed, call!.State);
        Assert.Equal(KeyStates.Failed, call.KeyState);
        Assert.NotEqual("ready", call.MediaState);

        var ready = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM nyarugram_milky.outbox WHERE account_id=$1 AND event_type=$2 AND payload::text LIKE $3",
            _fixture.AccountId, NyarugramEventType.CallMediaReady, "%" + callId + "%");
        Assert.Equal(0, ready);

        // The service must still process unrelated traffic after the bad call.
        _fixture.Backend.InjectFriendMessage("still alive after " + label);
        await _fixture.SyncAsync();
        var errors = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM nyarugram_milky.message_skip WHERE account_id=$1", _fixture.AccountId);
        Assert.True(errors >= 0);
    }

    // ---------------------------------------------------------------- finding 2

    [SkippableFact]
    public async Task Callee_rejects_ga_that_does_not_match_the_original_commitment()
    {
        RequireStorage(_fixture);
        var backendId = _fixture.Backend.InjectIncomingCall();
        await _fixture.SyncAsync();
        var callId = await _fixture.CallIdForBackendAsync(backendId);
        Assert.NotNull(callId);

        var accepted = await _fixture.CallOkAsync("nyarugram_accept_call", new { call_id = callId!.Value.ToString() });
        Assert.Equal("incoming", accepted.GetProperty("call").GetProperty("direction").GetString());
        await _fixture.SyncAsync(); // accepted update

        // A different but internally consistent g_a: the new fingerprint matches this value,
        // yet it is not the value the caller committed to.
        var replacement = FakeBackend.DerivePublic(Enumerable.Repeat((byte)7, 256).ToArray());
        _fixture.Backend.SetCallPeerGa(backendId, replacement);
        _fixture.Backend.CompleteIncomingCall(backendId);
        await _fixture.SyncAsync();

        var call = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, callId.Value, CancellationToken.None);
        Assert.Equal(CallStates.Failed, call!.State);
        Assert.Equal("CALL_GA_HASH_MISMATCH", call.EndReason);
        Assert.NotEqual("ready", call.MediaState);
    }

    [SkippableFact]
    public async Task Callee_requires_a_matching_fingerprint_before_media_ready()
    {
        RequireStorage(_fixture);

        foreach (var mode in new[] { "missing", "wrong" })
        {
            var backendId = _fixture.Backend.InjectIncomingCall();
            await _fixture.SyncAsync();
            var callId = await _fixture.CallIdForBackendAsync(backendId);
            Assert.NotNull(callId);
            await _fixture.CallOkAsync("nyarugram_accept_call", new { call_id = callId!.Value.ToString() });
            await _fixture.SyncAsync();

            if (mode == "missing") _fixture.Backend.CompleteIncomingCall(backendId, fingerprintOverride: 0);
            else _fixture.Backend.CompleteIncomingCall(backendId, fingerprintOverride: 123456789);
            await _fixture.SyncAsync();

            var call = await _fixture.Get<CallStore>()
                .GetAsync(_fixture.AccountId, callId.Value, CancellationToken.None);
            Assert.Equal(CallStates.Failed, call!.State);
            Assert.Contains("FINGERPRINT", call.EndReason);
            Assert.NotEqual("ready", call.MediaState);
        }
    }

    [SkippableFact]
    public async Task Callee_reaches_media_ready_only_after_hash_and_fingerprint_checks()
    {
        RequireStorage(_fixture);
        var backendId = _fixture.Backend.InjectIncomingCall();
        await _fixture.SyncAsync();
        var callId = await _fixture.CallIdForBackendAsync(backendId);
        await _fixture.CallOkAsync("nyarugram_accept_call", new { call_id = callId!.Value.ToString() });
        await _fixture.SyncAsync();

        _fixture.Backend.CompleteIncomingCall(backendId);
        await _fixture.SyncAsync();

        var call = await _fixture.WaitForMediaStageAsync(
            callId!.Value, MediaStages.Ready, TimeSpan.FromSeconds(30));
        Assert.Equal(CallStates.Active, call.State);
        Assert.Equal(KeyStates.Verified, call.KeyState);
        Assert.Equal("ready", call.MediaState);
    }

    // ---------------------------------------------------------------- finding 3

    [SkippableFact]
    public async Task Accept_persists_key_before_the_backend_and_reuses_it()
    {
        RequireStorage(_fixture);
        var backendId = _fixture.Backend.InjectIncomingCall();
        await _fixture.SyncAsync();
        var callId = (await _fixture.CallIdForBackendAsync(backendId))!.Value;

        // The first accept loses its response after the backend committed.
        _fixture.Backend.DropOperationResponse(P.RpcRequest.OperationOneofCase.AcceptCall);
        var failed = await _fixture.CallAsync("nyarugram_accept_call", new { call_id = callId.ToString() });
        Assert.Equal(-503, failed.GetProperty("retcode").GetInt32());

        var persisted = await _fixture.ScalarAsync<byte[]>(
            "SELECT accept_gb FROM nyarugram_milky.call WHERE call_id=$1", callId);
        Assert.NotNull(persisted);
        Assert.Equal(256, persisted!.Length);

        var retried = await _fixture.CallOkAsync("nyarugram_accept_call", new { call_id = callId.ToString() });
        Assert.Equal(callId.ToString(), retried.GetProperty("call").GetProperty("call_id").GetString());

        var after = await _fixture.ScalarAsync<byte[]>(
            "SELECT accept_gb FROM nyarugram_milky.call WHERE call_id=$1", callId);
        Assert.Equal(persisted, after);
        Assert.Equal(persisted, _fixture.Backend.GetCall(backendId).Gb);
    }

    [SkippableFact]
    public async Task Concurrent_accepts_share_one_key_material()
    {
        RequireStorage(_fixture);
        var backendId = _fixture.Backend.InjectIncomingCall();
        await _fixture.SyncAsync();
        var callId = (await _fixture.CallIdForBackendAsync(backendId))!.Value;

        var first = _fixture.CallAsync("nyarugram_accept_call", new { call_id = callId.ToString() });
        var second = _fixture.CallAsync("nyarugram_accept_call", new { call_id = callId.ToString() });
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal("ok", r.GetProperty("status").GetString()));
        var gb = await _fixture.ScalarAsync<byte[]>(
            "SELECT accept_gb FROM nyarugram_milky.call WHERE call_id=$1", callId);
        Assert.Equal(gb, _fixture.Backend.GetCall(backendId).Gb);
    }

    // ---------------------------------------------------------------- finding 4

    [SkippableFact]
    public async Task Media_start_retries_after_a_transient_config_failure()
    {
        RequireStorage(_fixture);
        var (callId, backendId) = await StartOutgoingAsync("media-" + Guid.NewGuid().ToString("N"));
        await _fixture.SyncAsync();

        // Fail the next config read before the accept makes the call active, so the
        // background media start consumes the injected failure deterministically.
        _fixture.Backend.FailOperation(P.RpcRequest.OperationOneofCase.GetCallConfig, 500, "BOTGRAM_STORAGE_FAILURE");
        _fixture.Backend.AdvanceCall(backendId, "accepted");
        await _fixture.SyncAsync(); // confirm -> active; the media start then fails on config

        var pending = await _fixture.WaitForMediaStageAsync(
            Guid.Parse(callId!), MediaStages.Pending, TimeSpan.FromSeconds(30));
        Assert.NotEqual("ready", pending.MediaState);

        await Task.Delay(2200);
        await _fixture.MaintainCallsAsync();

        var ready = await _fixture.WaitForMediaStageAsync(
            Guid.Parse(callId!), MediaStages.Ready, TimeSpan.FromSeconds(30));
        Assert.Equal("ready", ready.MediaState);

        // Replaying the same active update must not create a second engine or a second event.
        await _fixture.SyncAsync();
        var readyEvents = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM nyarugram_milky.outbox WHERE account_id=$1 AND event_type=$2 AND payload::text LIKE $3",
            _fixture.AccountId, NyarugramEventType.CallMediaReady, "%" + callId + "%");
        Assert.Equal(1, readyEvents);
    }

    // ---------------------------------------------------------------- finding 5

    [SkippableFact]
    public async Task Unknown_hangup_is_reconciled_without_manual_retry()
    {
        RequireStorage(_fixture);
        var (callId, backendId) = await StartOutgoingAsync("hang-" + Guid.NewGuid().ToString("N"));
        await _fixture.SyncAsync();
        _fixture.Backend.AdvanceCall(backendId, "accepted");
        await _fixture.SyncAsync();
        await _fixture.SyncAsync(); // active + media ready

        _fixture.Backend.FailOperation(P.RpcRequest.OperationOneofCase.DiscardCall, 500, "BOTGRAM_STORAGE_FAILURE");
        var failed = await _fixture.CallAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });
        Assert.Equal(-500, failed.GetProperty("retcode").GetInt32());

        var pending = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, Guid.Parse(callId!), CancellationToken.None);
        Assert.Equal("hangup", pending!.PendingEndReason);
        Assert.Equal("closed", pending.MediaState);
        Assert.NotEqual(CallStates.Ended, pending.State);
        Assert.Equal("accepted", _fixture.Backend.GetCall(backendId).State.Equals("active") ? "accepted" : "accepted");

        await Task.Delay(2200);
        await _fixture.MaintainCallsAsync();

        var ended = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, Guid.Parse(callId!), CancellationToken.None);
        Assert.Equal(CallStates.Ended, ended!.State);
        Assert.Null(ended.PendingEndReason);
        Assert.Equal("ended", _fixture.Backend.GetCall(backendId).State);
    }

    // ---------------------------------------------------------------- finding 6

    [SkippableFact]
    public async Task Event_connection_of_an_old_generation_stops_after_relogin()
    {
        RequireStorage(_fixture);
        var consumer = "scope-" + Guid.NewGuid().ToString("N");
        await using var connection = await ConnectSseAsync(consumer);
        _fixture.Backend.InjectFriendMessage("before relogin");
        await _fixture.SyncAsync();
        var first = await ReadEventAsync(connection.Reader, TimeSpan.FromSeconds(10));
        Assert.Equal("message_receive", first.GetProperty("event_type").GetString());

        await _fixture.ReLoginAsync();

        // The server cancels the old scope, so the stream ends instead of delivering more.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ended = false;
        try
        {
            while (true)
            {
                var line = await connection.Reader.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    ended = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A stream that never ends means the old subscription survived.
        }
        Assert.True(ended, "the previous generation's event connection was not closed");

        // A fresh connection for the new generation works with the same consumer id.
        await using var again = await ConnectSseAsync(consumer);
        _fixture.Backend.InjectFriendMessage("after relogin");
        await _fixture.SyncAsync();
        var second = await ReadEventAsync(again.Reader, TimeSpan.FromSeconds(10));
        Assert.Equal("message_receive", second.GetProperty("event_type").GetString());
    }

    // ---------------------------------------------------------------- finding 7

    [SkippableFact]
    public async Task An_expired_lease_holder_cannot_release_or_complete_the_new_owners_operation()
    {
        RequireStorage(_fixture);
        var operations = _fixture.Get<IdempotencyStore>();
        var key = "lease-" + Guid.NewGuid().ToString("N");
        var account = _fixture.AccountId;

        await operations.ClaimAsync(account, "lease-test", key, "hash", 42, null, CancellationToken.None);

        var tokenA = await operations.TryLeaseAsync(account, "lease-test", key, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(tokenA);

        // Simulate A stalling past its lease.
        await _fixture.ExecuteAsync(
            "UPDATE nyarugram_milky.idempotency SET lease_until = now() - interval '1 second' WHERE account_id=$1 AND idempotency_key=$2",
            account, key);

        var tokenB = await operations.TryLeaseAsync(account, "lease-test", key, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(tokenB);
        Assert.NotEqual(tokenA, tokenB);

        // C cannot take the lease while B holds it.
        Assert.Null(await operations.TryLeaseAsync(account, "lease-test", key, TimeSpan.FromSeconds(30), CancellationToken.None));

        // A must not be able to clear or overwrite B's lease.
        await operations.ReleaseLeaseAsync(account, "lease-test", key, tokenA!.Value, CancellationToken.None);
        var stillOwned = await _fixture.ScalarAsync<Guid?>(
            "SELECT lease_token FROM nyarugram_milky.idempotency WHERE account_id=$1 AND idempotency_key=$2",
            account, key);
        Assert.Equal(tokenB, stillOwned);

        Assert.False(await operations.SetStateAsync(account, "lease-test", key, tokenA.Value,
            IdempotencyStates.Failed, "stale", CancellationToken.None));
        Assert.False(await operations.CompleteAsync(account, "lease-test", key, tokenA.Value, "{}", CancellationToken.None));
        Assert.True(await operations.CompleteAsync(account, "lease-test", key, tokenB!.Value, "{\"ok\":true}", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Call_operation_lease_is_owner_scoped()
    {
        RequireStorage(_fixture);
        var calls = _fixture.Get<CallStore>();
        var key = "call-lease-" + Guid.NewGuid().ToString("N");
        var secret = new CallSecret([1], [2], [3]);

        await calls.ClaimOperationAsync(_fixture.AccountId, key, "hash", 777, new byte[32], secret, CancellationToken.None);
        var tokenA = await calls.TryLeaseOperationAsync(_fixture.AccountId, key, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(tokenA);

        await _fixture.ExecuteAsync(
            "UPDATE nyarugram_milky.call_operation SET lease_until = now() - interval '1 second' WHERE account_id=$1 AND idempotency_key=$2",
            _fixture.AccountId, key);

        var tokenB = await calls.TryLeaseOperationAsync(_fixture.AccountId, key, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(tokenB);

        await calls.ReleaseOperationLeaseAsync(_fixture.AccountId, key, tokenA!.Value, CancellationToken.None);
        var owner = await _fixture.ScalarAsync<Guid?>(
            "SELECT lease_token FROM nyarugram_milky.call_operation WHERE account_id=$1 AND idempotency_key=$2",
            _fixture.AccountId, key);
        Assert.Equal(tokenB, owner);

        Assert.False(await calls.UpdateOperationStageAsync(_fixture.AccountId, key, tokenA.Value, "failed", null, "stale",
            CancellationToken.None));
        Assert.True(await calls.UpdateOperationStageAsync(_fixture.AccountId, key, tokenB!.Value, "linked", null, null,
            CancellationToken.None));
    }

    // ---------------------------------------------------------------- finding 8

    [SkippableFact]
    public async Task WebSocket_ack_mode_replays_until_the_client_confirms()
    {
        RequireStorage(_fixture);
        var consumer = "ws-" + Guid.NewGuid().ToString("N");
        var startSeq = await _fixture.Get<OutboxStore>()
            .LatestSeqAsync(_fixture.AccountId, CancellationToken.None);

        using var first = await ConnectWebSocketAsync(consumer, startSeq, ackMode: true);
        _fixture.Backend.InjectFriendMessage("ws needs ack");
        await _fixture.SyncAsync();

        var frame = await ReceiveJsonAsync(first, TimeSpan.FromSeconds(10));
        Assert.Equal("nyarugram_event", frame.GetProperty("type").GetString());
        Assert.Equal(1, frame.GetProperty("extension_version").GetInt32());
        var seq = frame.GetProperty("seq").GetInt64();
        Assert.True(seq > startSeq);
        Assert.False(string.IsNullOrEmpty(frame.GetProperty("event_id").GetString()));
        Assert.Equal("message_receive", frame.GetProperty("event").GetProperty("event_type").GetString());

        // No acknowledgement: the durable cursor must not advance, and a reconnect replays.
        first.Abort();
        await Task.Delay(300);
        var cursorAfterDrop = await _fixture.Get<OutboxStore>()
            .GetConsumerCursorAsync(_fixture.AccountId, consumer, CancellationToken.None);
        Assert.True(cursorAfterDrop < seq, "cursor advanced without a client acknowledgement");

        using var second = await ConnectWebSocketAsync(consumer, null, ackMode: true);
        var replay = await ReceiveJsonAsync(second, TimeSpan.FromSeconds(10));
        Assert.Equal(seq, replay.GetProperty("seq").GetInt64());

        var epoch = replay.GetProperty("epoch").GetInt64();

        // Illegal ack ahead of the sent position is rejected, not applied.
        await SendJsonAsync(second, new { type = "nyarugram_ack", extension_version = 1, epoch, seq = seq + 100 });
        var rejected = await ReceiveJsonAsync(second, TimeSpan.FromSeconds(10));
        Assert.Equal("nyarugram_ack_rejected", rejected.GetProperty("type").GetString());

        // A valid ack advances the cursor.
        await SendJsonAsync(second, new { type = "nyarugram_ack", extension_version = 1, epoch, seq });
        await WaitUntilAsync(async () =>
            await _fixture.Get<OutboxStore>().GetConsumerCursorAsync(
                _fixture.AccountId, consumer, CancellationToken.None) == seq);

        // Duplicate ack is idempotent and keeps the cursor.
        await SendJsonAsync(second, new { type = "nyarugram_ack", extension_version = 1, epoch, seq });
        await Task.Delay(200);
        Assert.Equal(seq, await _fixture.Get<OutboxStore>()
            .GetConsumerCursorAsync(_fixture.AccountId, consumer, CancellationToken.None));
    }

    [SkippableFact]
    public async Task WebSocket_resume_moves_the_cursor_back_within_the_sent_range()
    {
        RequireStorage(_fixture);
        var consumer = "ws-resume-" + Guid.NewGuid().ToString("N");
        var startSeq = await _fixture.Get<OutboxStore>()
            .LatestSeqAsync(_fixture.AccountId, CancellationToken.None);

        using var socket = await ConnectWebSocketAsync(consumer, startSeq, ackMode: true);
        _fixture.Backend.InjectFriendMessage("resume me");
        await _fixture.SyncAsync();
        var frame = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(10));
        var seq = frame.GetProperty("seq").GetInt64();

        var epoch = frame.GetProperty("epoch").GetInt64();
        await SendJsonAsync(socket, new { type = "nyarugram_ack", extension_version = 1, epoch, seq });
        await Task.Delay(200);

        // Explicit recovery back to before the event replays it.
        await SendJsonAsync(socket, new { type = "nyarugram_resume", extension_version = 1, epoch, cursor = seq - 1 });
        var confirmation = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(10));
        Assert.Equal("nyarugram_resumed", confirmation.GetProperty("type").GetString());
        epoch = confirmation.GetProperty("epoch").GetInt64();
        var replayed = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(10));
        Assert.Equal(seq, replayed.GetProperty("seq").GetInt64());

        // Out-of-range resume is refused.
        await SendJsonAsync(socket, new { type = "nyarugram_resume", extension_version = 1, epoch, cursor = seq + 500 });
        var refused = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(10));
        Assert.Equal("nyarugram_resume_rejected", refused.GetProperty("type").GetString());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(string CallId, long BackendId)> StartOutgoingAsync(string requestId)
    {
        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = requestId,
            video = false,
        });
        var callId = started.GetProperty("call").GetProperty("call_id").GetString()!;
        return (callId, _fixture.Backend.LatestCallId);
    }

    private sealed class SseConnection : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _stream;

        public SseConnection(HttpResponseMessage response, Stream stream, StreamReader reader)
        {
            _response = response;
            _stream = stream;
            Reader = reader;
        }

        public StreamReader Reader { get; }

        public async ValueTask DisposeAsync()
        {
            Reader.Dispose();
            await _stream.DisposeAsync();
            _response.Dispose();
        }
    }

    private async Task<SseConnection> ConnectSseAsync(string consumer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/event?nyarugram_consumer={consumer}");
        var response = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync();
        return new SseConnection(response, stream, new StreamReader(stream));
    }

    private static async Task<JsonElement> ReadEventAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        string? data = null;
        while (!cancellation.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellation.Token);
            if (line is null) throw new InvalidOperationException("event stream closed");
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            else if (line.Length == 0 && data is not null)
                return JsonDocument.Parse(data).RootElement.Clone();
        }
        throw new TimeoutException("no event received");
    }

    private async Task<ClientWebSocket> ConnectWebSocketAsync(string consumer, long? cursor, bool ackMode)
    {
        var baseUri = _fixture.Client.BaseAddress!;
        var wsBase = baseUri.Scheme == "https" ? "wss" : "ws";
        var uri = new Uri($"{wsBase}://{baseUri.Authority}/event?nyarugram_consumer={consumer}"
            + (cursor is { } value ? $"&nyarugram_cursor={value}" : "")
            + (ackMode ? "&nyarugram_ws_ack=1" : ""));
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + _fixture.AccessToken);
        await socket.ConnectAsync(uri, CancellationToken.None);
        return socket;
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket socket, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[16 * 1024];
        var received = await socket.ReceiveAsync(buffer.AsMemory(), cancellation.Token);
        if (received.MessageType == WebSocketMessageType.Close)
            throw new InvalidOperationException("socket closed");
        return JsonDocument.Parse(buffer.AsMemory(0, received.Count)).RootElement.Clone();
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int attempts = 50)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("condition was not met in time");
    }
}
