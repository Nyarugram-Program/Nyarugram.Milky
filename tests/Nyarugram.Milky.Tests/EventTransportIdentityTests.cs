using System.Net.WebSockets;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;
using Xunit;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// SSE/WS connection identity and stable-consumer contract. These tests use real
/// HTTP connections against the adapter host and a real PostgreSQL schema.
/// </summary>
[Collection("adapter")]
public sealed class EventTransportIdentityTests
{
    private readonly AdapterFixture _fixture;

    public EventTransportIdentityTests(AdapterFixture fixture) => _fixture = fixture;

    private static void RequireStorage(AdapterFixture fixture)
    {
        if (fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {fixture.StartupError.Message}");
    }

    private static string ExpectedAdminIdentity(Guid accountId) => $"admin:{accountId:N}";

    private static string SseUrl(string consumer, string expectedIdentity) =>
        $"/event?nyarugram_consumer={Uri.EscapeDataString(consumer)}" +
        $"&nyarugram_expected_identity={Uri.EscapeDataString(expectedIdentity)}";

    private static async Task<string> ReadSseDataAsync(
        HttpResponseMessage response, string contains, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                data.AppendLine(line[6..]);
            if (line.Length == 0 && data.ToString().Contains(contains, StringComparison.Ordinal))
                return data.ToString();
        }
        throw new Xunit.Sdk.XunitException(
            $"SSE stream ended before an event containing '{contains}' arrived; data={data}");
    }


    private async Task<ClientWebSocket> ConnectWebSocketAsync(
        Guid expectedAccount, string consumer, CancellationToken ct)
    {
        var baseAddress = _fixture.Client.BaseAddress!;
        var builder = new UriBuilder("ws", baseAddress.Host, baseAddress.Port, "/event")
        {
            Query = "nyarugram_consumer=" + Uri.EscapeDataString(consumer) +
                    "&nyarugram_expected_identity=" +
                    Uri.EscapeDataString(ExpectedAdminIdentity(expectedAccount)) +
                    "&nyarugram_ws_ack=1",
        };
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_fixture.AccessToken}");
        await socket.ConnectAsync(builder.Uri, ct);
        return socket;
    }

    [SkippableFact]
    public async Task Named_sse_consumer_recovers_events_after_a_zero_event_disconnect()
    {
        RequireStorage(_fixture);
        var consumer = "sse-zero-" + Guid.NewGuid().ToString("N");
        var identity = ExpectedAdminIdentity(_fixture.AccountId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // First connection establishes the durable consumer marker, then drops
        // before any event is delivered.
        using (var first = await _fixture.Client.GetAsync(
                   SseUrl(consumer, identity), HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            await using var stream = await first.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var hello = await reader.ReadLineAsync(timeout.Token);
            Assert.Equal(": connected", hello);
        }

        // Let RequestAborted persist the initial position.
        await Task.Delay(200, timeout.Token);

        const string text = "sse zero-event recovery";
        _fixture.Backend.InjectFriendMessage(text);
        await _fixture.SyncAsync();

        using var second = await _fixture.Client.GetAsync(
            SseUrl(consumer, identity), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var payload = await ReadSseDataAsync(second, text, timeout.Token);
        Assert.Contains(text, payload, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Different_named_sse_consumers_do_not_overwrite_each_other()
    {
        RequireStorage(_fixture);
        var firstConsumer = "sse-a-" + Guid.NewGuid().ToString("N");
        var secondConsumer = "sse-b-" + Guid.NewGuid().ToString("N");
        var identity = ExpectedAdminIdentity(_fixture.AccountId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var first = await _fixture.Client.GetAsync(
            SseUrl(firstConsumer, identity), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var second = await _fixture.Client.GetAsync(
            SseUrl(secondConsumer, identity), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        const string text = "sse independent consumers";
        _fixture.Backend.InjectFriendMessage(text);
        await _fixture.SyncAsync();

        var firstPayload = await ReadSseDataAsync(first, text, timeout.Token);
        var secondPayload = await ReadSseDataAsync(second, text, timeout.Token);
        Assert.Contains(text, firstPayload, StringComparison.Ordinal);
        Assert.Contains(text, secondPayload, StringComparison.Ordinal);

        var outbox = _fixture.Get<OutboxStore>();
        var firstPosition = await outbox.GetConsumerCursorAsync(
            _fixture.AccountId, firstConsumer, CancellationToken.None);
        var secondPosition = await outbox.GetConsumerCursorAsync(
            _fixture.AccountId, secondConsumer, CancellationToken.None);
        Assert.True(firstPosition >= 0);
        Assert.True(secondPosition >= 0);
    }

    [SkippableFact]
    public async Task Event_connection_rejects_a_mismatched_expected_account()
    {
        RequireStorage(_fixture);
        var consumer = "identity-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var wrongAdmin = await _fixture.Client.GetAsync(
            SseUrl(consumer, ExpectedAdminIdentity(Guid.NewGuid())),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.Conflict, wrongAdmin.StatusCode);

        using var wrongBusiness = await _fixture.Client.GetAsync(
            SseUrl(consumer, "business:999999999"),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.Conflict, wrongBusiness.StatusCode);

        using var correctAdmin = await _fixture.Client.GetAsync(
            SseUrl(consumer, ExpectedAdminIdentity(_fixture.AccountId)),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, correctAdmin.StatusCode);

        using var correctBusiness = await _fixture.Client.GetAsync(
            SseUrl(consumer, $"business:{FakeBackend.AccountUin}"),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, correctBusiness.StatusCode);
    }

    [SkippableFact]
    public async Task Account_switch_invalidates_the_old_expected_identity()
    {
        RequireStorage(_fixture);
        var sessions = _fixture.Get<SessionManager>();
        var accountA = _fixture.AccountId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await sessions.LogoutAsync(timeout.Token);
            var contextB = await sessions.LoginAsync("bot2", "secret", null, timeout.Token);
            var accountB = contextB.Account.AccountId;

            using var oldIdentity = await _fixture.Client.GetAsync(
                SseUrl("switch-old-" + Guid.NewGuid().ToString("N"), ExpectedAdminIdentity(accountA)),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, oldIdentity.StatusCode);

            using var newIdentity = await _fixture.Client.GetAsync(
                SseUrl("switch-new-" + Guid.NewGuid().ToString("N"), ExpectedAdminIdentity(accountB)),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, newIdentity.StatusCode);
        }
        finally
        {
            await sessions.LogoutAsync(CancellationToken.None);
            await sessions.LoginAsync("bot", "secret", null, CancellationToken.None);
        }
    }
    [SkippableFact]
    public async Task WebSocket_closes_and_rejects_the_old_identity_after_an_account_switch()
    {
        RequireStorage(_fixture);
        var sessions = _fixture.Get<SessionManager>();
        var accountA = _fixture.AccountId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            using var socketA = await ConnectWebSocketAsync(
                accountA, "ws-switch-" + Guid.NewGuid().ToString("N"), timeout.Token);

            await sessions.LogoutAsync(timeout.Token);
            var contextB = await sessions.LoginAsync("bot2", "secret", null, timeout.Token);
            var accountB = contextB.Account.AccountId;

            var buffer = new byte[256];
            try
            {
                var result = await socketA.ReceiveAsync(buffer, timeout.Token);
                Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            }
            catch (WebSocketException)
            {
                // A hard abort is also a valid close for this contract.
            }

            await Assert.ThrowsAnyAsync<WebSocketException>(() =>
                ConnectWebSocketAsync(
                    accountA, "ws-old-" + Guid.NewGuid().ToString("N"), timeout.Token));
            using var socketB = await ConnectWebSocketAsync(
                accountB, "ws-new-" + Guid.NewGuid().ToString("N"), timeout.Token);
            Assert.Equal(WebSocketState.Open, socketB.State);
        }
        finally
        {
            await sessions.LogoutAsync(CancellationToken.None);
            await sessions.LoginAsync("bot", "secret", null, CancellationToken.None);
        }
    }

}
