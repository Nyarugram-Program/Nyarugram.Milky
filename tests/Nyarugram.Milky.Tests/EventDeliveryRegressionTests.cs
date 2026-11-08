using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;
using Xunit;

namespace Nyarugram.Milky.Tests;

[Collection("adapter")]
public sealed class EventDeliveryRegressionTests
{
    private readonly AdapterFixture _fixture;

    public EventDeliveryRegressionTests(AdapterFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Wire_acknowledgements_cannot_cross_a_recovery_epoch()
    {
        RequireStorage();
        var outbox = _fixture.Get<OutboxStore>();
        var consumer = "epoch-" + Guid.NewGuid().ToString("N");
        var start = await outbox.LatestSeqAsync(_fixture.AccountId, CancellationToken.None);
        using var socket = await ConnectAsync(consumer, start);
        _fixture.Backend.InjectFriendMessage("epoch regression");
        await _fixture.SyncAsync();
        var original = await ReceiveAsync(socket);
        var seq = original.GetProperty("seq").GetInt64();
        var epoch = original.GetProperty("epoch").GetInt64();

        await SendAsync(socket, new { type = "nyarugram_ack", seq, epoch });
        await WaitForCursorAsync(consumer, seq);
        await SendAsync(socket, new { type = "nyarugram_resume", cursor = start, epoch });
        var resumed = await ReceiveAsync(socket);
        Assert.Equal("nyarugram_resumed", resumed.GetProperty("type").GetString());
        var nextEpoch = resumed.GetProperty("epoch").GetInt64();
        Assert.True(nextEpoch > epoch);
        var replay = await ReceiveAsync(socket);
        Assert.Equal(seq, replay.GetProperty("seq").GetInt64());
        Assert.Equal(nextEpoch, replay.GetProperty("epoch").GetInt64());

        foreach (var invalid in new object[]
        {
            new { type = "nyarugram_ack", seq, epoch },
            new { type = "nyarugram_ack", seq },
            new { type = "nyarugram_ack", seq, epoch = nextEpoch + 1 },
            new { type = "nyarugram_resume", cursor = start, epoch },
            new { type = "nyarugram_resume", cursor = start },
        })
        {
            await SendAsync(socket, invalid);
            var rejected = await ReceiveAsync(socket);
            Assert.EndsWith("_rejected", rejected.GetProperty("type").GetString());
            Assert.Equal(nextEpoch, rejected.GetProperty("epoch").GetInt64());
            var position = await outbox.GetConsumerCursorEpochAsync(_fixture.AccountId, consumer, CancellationToken.None);
            Assert.Equal(start, position.Seq);
            Assert.Equal(nextEpoch, position.Epoch);
        }

        await SendAsync(socket, new { type = "nyarugram_ack", seq, epoch = nextEpoch });
        await WaitForCursorAsync(consumer, seq);
        socket.Abort();
    }

    [SkippableFact]
    public async Task A_blocked_writer_is_removed_and_a_healthy_socket_receives_the_next_page()
    {
        RequireStorage();
        var outbox = _fixture.Get<OutboxStore>();
        var bus = _fixture.Get<EventBus>();
        var context = _fixture.Runtime.Require();
        var start = await outbox.LatestSeqAsync(_fixture.AccountId, CancellationToken.None);
        var blockedName = "blocked-" + Guid.NewGuid().ToString("N");
        using var blocked = bus.Subscribe("paused-transport", blockedName, context.Scope, _fixture.AccountId, start);
        using var transport = CancellationTokenSource.CreateLinkedTokenSource(blocked.Stopped);
        var pendingWrite = Task.Delay(Timeout.Infinite, transport.Token);
        using var socket = await ConnectAsync("healthy-" + Guid.NewGuid().ToString("N"), start);
        var expected = _fixture.Get<RuntimeOptions>().ReplayBatchSize + 10;
        for (var i = 0; i < expected; i++) _fixture.Backend.InjectFriendMessage("blocked writer " + i);
        await _fixture.SyncAsync();

        var seen = new HashSet<long>();
        while (seen.Count < expected)
        {
            var frame = await ReceiveAsync(socket);
            var seq = frame.GetProperty("seq").GetInt64();
            seen.Add(seq);
            await SendAsync(socket, new { type = "nyarugram_ack", seq, epoch = frame.GetProperty("epoch").GetInt64() });
        }
        Assert.Equal(expected, seen.Count);
        Assert.True(blocked.Overflowed);
        Assert.DoesNotContain(blockedName, bus.Consumers(_fixture.AccountId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingWrite);
        socket.Abort();
    }

    [SkippableFact]
    public async Task Aborting_an_idle_websocket_removes_its_subscription()
    {
        RequireStorage();
        var bus = _fixture.Get<EventBus>();
        var consumer = "idle-" + Guid.NewGuid().ToString("N");
        var start = await _fixture.Get<OutboxStore>().LatestSeqAsync(_fixture.AccountId, CancellationToken.None);
        using var socket = await ConnectAsync(consumer, start);
        await WaitUntilAsync(() => Task.FromResult(bus.Consumers(_fixture.AccountId).Contains(consumer)));
        socket.Abort();
        await WaitUntilAsync(() => Task.FromResult(!bus.Consumers(_fixture.AccountId).Contains(consumer)));
    }

    [SkippableFact]
    public async Task Plain_websocket_events_keep_the_standard_milky_envelope()
    {
        RequireStorage();
        var start = await _fixture.Get<OutboxStore>().LatestSeqAsync(_fixture.AccountId, CancellationToken.None);
        using var socket = await ConnectAsync("plain-" + Guid.NewGuid().ToString("N"), start, framed: false);
        _fixture.Backend.InjectFriendMessage("plain compatibility");
        await _fixture.SyncAsync();
        var frame = await ReceiveAsync(socket);
        Assert.Equal("message_receive", frame.GetProperty("event_type").GetString());
        Assert.False(frame.TryGetProperty("epoch", out _));
        Assert.False(frame.TryGetProperty("type", out _));
        socket.Abort();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overflow_cancels_and_unregisters_without_waiting_for_a_reader(bool replay)
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var account = Guid.NewGuid();
        using var subscription = bus.Subscribe("paused", "paused", "scope", account, 0, capacity: 1);
        var first = new EventEnvelope(1, "one", "test", "{}");
        var second = new EventEnvelope(2, "two", "test", "{}");
        if (replay) subscription.Replay([first, second]);
        else
        {
            bus.Publish(account, first);
            bus.Publish(account, second);
        }
        Assert.True(subscription.Overflowed);
        Assert.True(subscription.Stopped.IsCancellationRequested);
        Assert.Equal(0, bus.Count);
        Assert.Equal(-1, bus.MinSentCursor(account));
        subscription.Cancel();
        subscription.Dispose();
    }

    private void RequireStorage()
    {
        if (_fixture.StartupError is not null)
            throw new SkipException("PostgreSQL is unavailable: " + _fixture.StartupError.Message);
    }

    private async Task<ClientWebSocket> ConnectAsync(string consumer, long cursor, bool framed = true)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + _fixture.AccessToken);
        var uri = new Uri($"ws://{_fixture.Client.BaseAddress!.Authority}/event?nyarugram_consumer={consumer}&nyarugram_cursor={cursor}"
            + (framed ? "&nyarugram_ws_ack=1" : ""));
        await socket.ConnectAsync(uri, CancellationToken.None);
        return socket;
    }

    private async Task WaitForCursorAsync(string consumer, long seq)
        => await WaitUntilAsync(async () =>
            await _fixture.Get<OutboxStore>().GetConsumerCursorAsync(_fixture.AccountId, consumer, CancellationToken.None) == seq);

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await predicate()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("event lifecycle did not reach the expected state");
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload)
        => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);
}
