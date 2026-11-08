using System.Net.WebSockets;
using System.Text.Json;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;
using Xunit;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Fourth-round review regressions:
/// 1. a consumer that never acknowledges must not stall delivery to its peers;
/// 2. a stale connection must not overwrite a rewind;
/// 3. a failed startup cleanup keeps retrying in periodic maintenance;
/// 4. hanging up closes media atomically and emits a media-closed event.
/// </summary>
[Collection("adapter")]
public sealed class Round5RegressionTests
{
    private readonly AdapterFixture _fixture;

    public Round5RegressionTests(AdapterFixture fixture) => _fixture = fixture;

    private static void RequireStorage(AdapterFixture fixture)
    {
        if (fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {fixture.StartupError.Message}");
    }

    // ------------------------------------------------------------------ finding 1

    [SkippableFact]
    public async Task A_consumer_that_never_acknowledges_does_not_stall_its_peers()
    {
        RequireStorage(_fixture);
        var silent = "silent-" + Guid.NewGuid().ToString("N");
        var active = "active-" + Guid.NewGuid().ToString("N");
        var outbox = _fixture.Get<OutboxStore>();
        var startSeq = await outbox.LatestSeqAsync(_fixture.AccountId, CancellationToken.None);

        // Two consumers connect. The silent one never sends an acknowledgement.
        using var silentSocket = await ConnectWebSocketAsync(silent, startSeq, ackMode: true);
        using var activeSocket = await ConnectWebSocketAsync(active, startSeq, ackMode: true);

        // Inject enough events to span more than one drain batch, and have the active consumer
        // acknowledge them all while the silent one acknowledges none.
        const int count = 30;
        for (var i = 0; i < count; i++)
            _fixture.Backend.InjectFriendMessage($"peer delivery {i}");
        await _fixture.SyncAsync();

        var seen = 0;
        for (var i = 0; i < count; i++)
        {
            var frame = await ReceiveJsonAsync(activeSocket, TimeSpan.FromSeconds(15));
            var seq = frame.GetProperty("seq").GetInt64();
            await SendJsonAsync(activeSocket, new { type = "nyarugram_ack", extension_version = 1, epoch = frame.GetProperty("epoch").GetInt64(), seq });
            seen++;
        }
        Assert.Equal(count, seen);
    }

    // ------------------------------------------------------------------ finding 2

    [SkippableFact]
    public async Task A_stale_connection_cannot_overwrite_a_rewind()
    {
        RequireStorage(_fixture);
        var consumer = "stale-" + Guid.NewGuid().ToString("N");
        var outbox = _fixture.Get<OutboxStore>();
        var accountId = _fixture.AccountId;

        // Establish a durable cursor at 100 via a direct write (the "old connection").
        await outbox.EnsureConsumerAsync(accountId, consumer, 100, CancellationToken.None);
        var before = await outbox.GetConsumerCursorEpochAsync(accountId, consumer, CancellationToken.None);
        Assert.Equal(100, before.Seq);

        // A rewind bumps the generation and lowers the position.
        var newEpoch = await outbox.RewindConsumerCursorAsync(accountId, consumer, 50, CancellationToken.None);
        Assert.True(newEpoch > before.Epoch);

        // The old connection's shutdown write-back (same old epoch, higher seq) must be ignored.
        await outbox.SetConsumerCursorAsync(accountId, consumer, 100, before.Epoch, CancellationToken.None);

        var after = await outbox.GetConsumerCursorEpochAsync(accountId, consumer, CancellationToken.None);
        Assert.Equal(50, after.Seq);
        Assert.Equal(newEpoch, after.Epoch);
    }

    // ------------------------------------------------------------------ finding 3

    [SkippableFact]
    public async Task A_failed_startup_cleanup_keeps_retrying_in_maintenance()
    {
        RequireStorage(_fixture);
        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = "call-stale-" + Guid.NewGuid().ToString("N"),
            video = false,
        });
        var callId = Guid.Parse(started.GetProperty("call").GetProperty("call_id").GetString()!);
        var backendCallId = _fixture.Backend.LatestCallId;

        // Simulate a previous process owning this call.
        await _fixture.ExecuteAsync(
            "UPDATE nyarugram_milky.call SET instance_id = 'previous-process' WHERE call_id = $1", callId);

        // The first startup cleanup discard fails transiently.
        _fixture.Backend.FailOperation(P.RpcRequest.OperationOneofCase.DiscardCall, 500, "BOTGRAM_STORAGE_FAILURE");
        await _fixture.Get<CallService>().ReconcileAsync(_fixture.Runtime.Require(), CancellationToken.None);

        var pending = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, callId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal("pending", pending!.ReconcileState);
        Assert.NotEqual("ended", _fixture.Backend.GetCall(backendCallId).State);

        // Fast-forward past the backoff so the periodic pass is due to retry.
        await _fixture.ExecuteAsync(
            "UPDATE nyarugram_milky.call SET reconcile_next_at = now() - interval '1 second' WHERE call_id = $1",
            callId);

        // Periodic maintenance retries the discard without a manual retry.
        await _fixture.MaintainCallsAsync();

        var after = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, callId, CancellationToken.None);
        Assert.Equal(CallStates.Ended, after!.State);
        Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
    }

    // ------------------------------------------------------------------ finding 4

    [SkippableFact]
    public async Task Hanging_up_closes_media_and_emits_a_media_closed_event()
    {
        RequireStorage(_fixture);
        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = "call-media-" + Guid.NewGuid().ToString("N"),
            video = false,
        });
        var callId = started.GetProperty("call").GetProperty("call_id").GetString()!;
        var backendCallId = _fixture.Backend.LatestCallId;

        // Drive the call to active + media ready.
        await _fixture.SyncAsync();
        _fixture.Backend.AdvanceCall(backendCallId, "accepted");
        await _fixture.SyncAsync();
        await _fixture.SyncAsync();

        var active = await _fixture.WaitForMediaStageAsync(
            Guid.Parse(callId), MediaStages.Ready, TimeSpan.FromSeconds(30));
        Assert.Equal("ready", active.MediaState);

        await _fixture.CallOkAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });

        var ended = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, Guid.Parse(callId), CancellationToken.None);
        Assert.Equal(CallStates.Ended, ended!.State);
        Assert.Equal("closed", ended.MediaState);
        Assert.Equal("closed", ended.MediaStage);

        var closedEvents = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM nyarugram_milky.outbox WHERE account_id=$1 AND event_type=$2 AND payload::text LIKE $3",
            _fixture.AccountId, "nyarugram_call_media_closed", "%" + callId + "%");
        Assert.True(closedEvents > 0, "no media_closed event was emitted on hangup");
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
}
