using System.Net.WebSockets;
using System.Text.Json;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;
using Xunit;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Third-round review regressions:
/// 1. accept key recovery after a lost response;
/// 2. backend line release after a local key failure;
/// 3. zero-cursor consumer reconnect does not lose unacknowledged events;
/// 4. explicit resume persists the rewind durably;
/// 5. a recovery range wider than one page is replayed in full.
/// </summary>
[Collection("adapter")]
public sealed class Round4RegressionTests
{
    private readonly AdapterFixture _fixture;

    public Round4RegressionTests(AdapterFixture fixture) => _fixture = fixture;

    private static void RequireStorage(AdapterFixture fixture)
    {
        if (fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {fixture.StartupError.Message}");
    }

    // ------------------------------------------------------------------ finding 1

    [SkippableFact]
    public async Task Lost_accept_response_still_derives_the_key_from_persisted_material()
    {
        RequireStorage(_fixture);
        var backendId = _fixture.Backend.InjectIncomingCall();
        await _fixture.SyncAsync();
        var callId = await _fixture.CallIdForBackendAsync(backendId);
        Assert.NotNull(callId);

        // The accept commits on the backend but the response is lost.
        _fixture.Backend.DropOperationResponse(P.RpcRequest.OperationOneofCase.AcceptCall);
        var failed = await _fixture.CallAsync("nyarugram_accept_call", new { call_id = callId!.Value.ToString() });
        Assert.Equal(-503, failed.GetProperty("retcode").GetInt32());

        // The accept-specific secret is durable even though the generic one was never promoted.
        var acceptSecret = await _fixture.ScalarAsync<byte[]>(
            "SELECT accept_secret_cipher FROM nyarugram_milky.call WHERE call_id=$1", callId!.Value);
        Assert.NotNull(acceptSecret);
        Assert.True(acceptSecret!.Length > 0);

        // The caller confirms: active update arrives and the callee must derive the key from
        // the accept material without a manual retry of accept_call.
        _fixture.Backend.CompleteIncomingCall(backendId);
        await _fixture.SyncAsync();

        var call = await _fixture.WaitForMediaStageAsync(
            callId.Value, MediaStages.Ready, TimeSpan.FromSeconds(30));
        Assert.Equal(CallStates.Active, call.State);
        Assert.Equal(KeyStates.Verified, call.KeyState);
        Assert.Equal("ready", call.MediaState);
    }

    // ------------------------------------------------------------------ finding 2

    [SkippableFact]
    public async Task Key_failure_releases_the_backend_line_so_the_peer_can_redial()
    {
        RequireStorage(_fixture);
        var backendId = _fixture.Backend.InjectIncomingCall();
        await _fixture.SyncAsync();
        var callId = await _fixture.CallIdForBackendAsync(backendId);
        Assert.NotNull(callId);
        await _fixture.CallOkAsync("nyarugram_accept_call", new { call_id = callId!.Value.ToString() });
        await _fixture.SyncAsync();

        // A g_a that does not match the original commitment makes the key derivation fail.
        var replacement = FakeBackend.DerivePublic(Enumerable.Repeat((byte)7, 256).ToArray());
        _fixture.Backend.SetCallPeerGa(backendId, replacement);
        _fixture.Backend.CompleteIncomingCall(backendId);
        await _fixture.SyncAsync();

        var call = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, callId!.Value, CancellationToken.None);
        Assert.Equal(CallStates.Failed, call!.State);

        // The backend must not stay busy: the failed call is discarded there.
        Assert.Equal("ended", _fixture.Backend.GetCall(backendId).State);
    }

    [SkippableFact]
    public async Task Key_failure_with_transient_discard_is_reconciled_by_maintenance()
    {
        RequireStorage(_fixture);
        var backendId = _fixture.Backend.InjectIncomingCall();
        await _fixture.SyncAsync();
        var callId = await _fixture.CallIdForBackendAsync(backendId);
        Assert.NotNull(callId);
        await _fixture.CallOkAsync("nyarugram_accept_call", new { call_id = callId!.Value.ToString() });
        await _fixture.SyncAsync();

        // The key fails AND the follow-up discard is transiently unavailable.
        _fixture.Backend.FailOperation(P.RpcRequest.OperationOneofCase.DiscardCall, 500, "BOTGRAM_STORAGE_FAILURE");
        var replacement = FakeBackend.DerivePublic(Enumerable.Repeat((byte)7, 256).ToArray());
        _fixture.Backend.SetCallPeerGa(backendId, replacement);
        _fixture.Backend.CompleteIncomingCall(backendId);
        await _fixture.SyncAsync();

        var call = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, callId!.Value, CancellationToken.None);
        Assert.Equal(CallStates.Failed, call!.State);
        // The line release is pending, not silently dropped.
        Assert.NotNull(call.PendingEndReason);

        // The maintenance worker retries the discard without a manual retry.
        await _fixture.MaintainCallsAsync();
        Assert.Equal("ended", _fixture.Backend.GetCall(backendId).State);

        var after = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, callId.Value, CancellationToken.None);
        Assert.Null(after!.PendingEndReason);
    }

    // ------------------------------------------------------------------ finding 3

    [SkippableFact]
    public async Task Zero_cursor_consumer_reconnect_replays_unacknowledged_events()
    {
        RequireStorage(_fixture);
        var consumer = "zc-" + Guid.NewGuid().ToString("N");
        var outbox = _fixture.Get<OutboxStore>();

        using var first = await ConnectWebSocketAsync(consumer, cursor: 0, ackMode: true);
        _fixture.Backend.InjectFriendMessage("first unacked");
        await _fixture.SyncAsync();

        var frame = await ReceiveJsonAsync(first, TimeSpan.FromSeconds(10));
        Assert.Equal("nyarugram_event", frame.GetProperty("type").GetString());
        var seq = frame.GetProperty("seq").GetInt64();

        // Drop without acknowledging: the durable cursor must stay at (or below) the start.
        first.Abort();
        await Task.Delay(300);

        // Reconnect with the same consumer id but no explicit cursor: it must resume from the
        // registered start, not from the live head.
        using var second = await ConnectWebSocketAsync(consumer, cursor: null, ackMode: true);
        var replayed = await ReceiveJsonAsync(second, TimeSpan.FromSeconds(10));
        Assert.Equal(seq, replayed.GetProperty("seq").GetInt64());
    }

    // ------------------------------------------------------------------ finding 4

    [SkippableFact]
    public async Task Explicit_resume_persists_the_rewind_durably()
    {
        RequireStorage(_fixture);
        var consumer = "rw-" + Guid.NewGuid().ToString("N");
        var outbox = _fixture.Get<OutboxStore>();
        var startSeq = await outbox.LatestSeqAsync(_fixture.AccountId, CancellationToken.None);

        using var socket = await ConnectWebSocketAsync(consumer, startSeq, ackMode: true);
        _fixture.Backend.InjectFriendMessage("resume target");
        await _fixture.SyncAsync();
        var frame = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(10));
        var seq = frame.GetProperty("seq").GetInt64();

        var epoch = frame.GetProperty("epoch").GetInt64();
        await SendJsonAsync(socket, new { type = "nyarugram_ack", extension_version = 1, epoch, seq });
        await WaitUntilAsync(async () =>
            await outbox.GetConsumerCursorAsync(_fixture.AccountId, consumer, CancellationToken.None) == seq);

        // Rewind below the acked position and confirm the durable cursor actually moved back.
        await SendJsonAsync(socket, new { type = "nyarugram_resume", extension_version = 1, epoch, cursor = seq - 1 });
        var resumed = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(10));
        Assert.Equal("nyarugram_resumed", resumed.GetProperty("type").GetString());
        await WaitUntilAsync(async () =>
            await outbox.GetConsumerCursorAsync(_fixture.AccountId, consumer, CancellationToken.None) == seq - 1);

        // Drop and reconnect: the rewind must survive, so the replayed event comes again.
        socket.Abort();
        await Task.Delay(300);
        using var second = await ConnectWebSocketAsync(consumer, cursor: null, ackMode: true);
        var again = await ReceiveJsonAsync(second, TimeSpan.FromSeconds(10));
        Assert.Equal(seq, again.GetProperty("seq").GetInt64());
    }

    // ------------------------------------------------------------------ finding 5

    [SkippableFact]
    public async Task Resume_over_a_range_wider_than_one_page_replays_everything()
    {
        RequireStorage(_fixture);
        var consumer = "pg-" + Guid.NewGuid().ToString("N");
        var outbox = _fixture.Get<OutboxStore>();

        // More than one drain batch (ReplayBatchSize = 200) but below the channel bound, so
        // the normal delivery does not trip the slow-consumer guard while the resume still has
        // to page through the whole range.
        const int target = 250;
        var startSeq = await outbox.LatestSeqAsync(_fixture.AccountId, CancellationToken.None);
        for (var i = 0; i < target; i++)
            _fixture.Backend.InjectFriendMessage($"bulk {i}");
        await _fixture.SyncAsync();

        using var socket = await ConnectWebSocketAsync(consumer, startSeq, ackMode: true);

        // Consume and acknowledge every event.
        var seen = new HashSet<long>();
        long epoch = 0;
        while (seen.Count < target)
        {
            var frame = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(15));
            var s = frame.GetProperty("seq").GetInt64();
            seen.Add(s);
            epoch = frame.GetProperty("epoch").GetInt64();
            await SendJsonAsync(socket, new { type = "nyarugram_ack", extension_version = 1, epoch, seq = s });
        }
        Assert.Equal(target, seen.Count);

        // A resume back to the start must replay the whole range in full, not just one page.
        await SendJsonAsync(socket, new { type = "nyarugram_resume", extension_version = 1, epoch, cursor = startSeq });
        var resumed = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(10));
        Assert.Equal("nyarugram_resumed", resumed.GetProperty("type").GetString());

        var replayed = new HashSet<long>();
        while (replayed.Count < target)
        {
            var frame = await ReceiveJsonAsync(socket, TimeSpan.FromSeconds(15));
            replayed.Add(frame.GetProperty("seq").GetInt64());
        }
        Assert.Equal(target, replayed.Count);
        Assert.Equal(seen, replayed);
    }

    // ------------------------------------------------------------------ helpers

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
