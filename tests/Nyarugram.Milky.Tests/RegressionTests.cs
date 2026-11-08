using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;
using Xunit;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Regressions for the nine review findings. Each test names the finding it covers and
/// asserts against the real PostgreSQL projection plus the in-process backend, never by
/// loosening the fake backend's contract.
/// </summary>
[Collection("adapter")]
public sealed class RegressionTests
{
    private readonly AdapterFixture _fixture;

    public RegressionTests(AdapterFixture fixture) => _fixture = fixture;

    private static void RequireStorage(AdapterFixture fixture)
    {
        if (fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {fixture.StartupError.Message}");
    }

    // ---------------------------------------------------------------- finding 1

    [SkippableFact]
    public async Task Account_operations_are_only_served_on_the_account_endpoint()
    {
        RequireStorage(_fixture);
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.Backend.BaseAddress) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FakeBackend.Token);

        var business = await PostProtoAsync(client, "/api/v1/rpc",
            new P.RpcRequest { ProtocolMajor = 1, RequestId = "probe1", AccountMe = new P.AccountMeRequest() });
        Assert.Equal(HttpStatusCode.NotImplemented, business.StatusCode);

        var account = await PostProtoAsync(client, "/api/v1/account",
            new P.RpcRequest { ProtocolMajor = 1, RequestId = "probe2", AccountMe = new P.AccountMeRequest() });
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        Assert.Equal(FakeBackend.AccountUin, P.RpcResponse.Parser.ParseFrom(account.Body).AccountMe.TlUserId);

        using var stale = new HttpClient { BaseAddress = new Uri(_fixture.Backend.BaseAddress) };
        stale.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "expired-token");
        var rejected = await PostProtoAsync(stale, "/api/v1/account",
            new P.RpcRequest { ProtocolMajor = 1, RequestId = "probe3", AccountMe = new P.AccountMeRequest() });
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [SkippableFact]
    public async Task Login_info_fails_when_the_session_changes_during_the_request()
    {
        RequireStorage(_fixture);
        var backend = _fixture.Backend;
        backend.AccountDelay = TimeSpan.FromMilliseconds(700);
        try
        {
            var inFlight = _fixture.CallAsync("get_login_info");
            await Task.Delay(150);
            // Simulate logout/account switch while the account request is in flight.
            _fixture.Runtime.Clear();

            var envelope = await inFlight;
            Assert.Equal("failed", envelope.GetProperty("status").GetString());
            Assert.Equal(MilkyRetCodeNotLoggedIn, envelope.GetProperty("retcode").GetInt32());
        }
        finally
        {
            backend.AccountDelay = TimeSpan.Zero;
            await _fixture.ReLoginAsync();
        }
    }

    // ---------------------------------------------------------------- finding 2

    [SkippableFact]
    public async Task Repeating_an_idempotency_key_replays_the_original_send()
    {
        RequireStorage(_fixture);
        var key = "send-" + Guid.NewGuid().ToString("N");
        var body = new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "text", data = new { text = "same key" } } },
            nyarugram_idempotency_key = key,
        };

        var first = await _fixture.CallOkAsync("send_private_message", body);
        var second = await _fixture.CallOkAsync("send_private_message", body);
        Assert.Equal(first.GetProperty("message_seq").GetInt64(), second.GetProperty("message_seq").GetInt64());

        // A third, new send must advance the backend counter by exactly one: the replay did
        // not create a second message.
        var third = await _fixture.CallOkAsync("send_private_message", new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "text", data = new { text = "another" } } },
        });
        Assert.Equal(first.GetProperty("message_seq").GetInt64() + 1, third.GetProperty("message_seq").GetInt64());
    }

    [SkippableFact]
    public async Task Reusing_an_idempotency_key_with_different_content_conflicts()
    {
        RequireStorage(_fixture);
        var key = "send-conflict-" + Guid.NewGuid().ToString("N");
        await _fixture.CallOkAsync("send_private_message", new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "text", data = new { text = "one" } } },
            nyarugram_idempotency_key = key,
        });

        var envelope = await _fixture.CallAsync("send_private_message", new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "text", data = new { text = "two" } } },
            nyarugram_idempotency_key = key,
        });
        Assert.Equal("failed", envelope.GetProperty("status").GetString());
        Assert.Equal(-409, envelope.GetProperty("retcode").GetInt32());
    }

    [SkippableFact]
    public async Task Lost_response_leaves_the_send_reconcilable_and_replays_the_same_message()
    {
        RequireStorage(_fixture);
        var key = "send-lost-" + Guid.NewGuid().ToString("N");
        var body = new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "text", data = new { text = "lost response" } } },
            nyarugram_idempotency_key = key,
        };

        _fixture.Backend.DropOperationResponse(P.RpcRequest.OperationOneofCase.SendMessage);
        var failed = await _fixture.CallAsync("send_private_message", body);
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal(-500, failed.GetProperty("retcode").GetInt32());
        Assert.Contains(key, failed.GetProperty("message").GetString());

        // The operation row still exists with the same random id, so replaying resolves it.
        var replay = await _fixture.CallOkAsync("send_private_message", body);
        var seq = replay.GetProperty("message_seq").GetInt64();

        var next = await _fixture.CallOkAsync("send_private_message", new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "text", data = new { text = "after recovery" } } },
        });
        Assert.Equal(seq + 1, next.GetProperty("message_seq").GetInt64());
    }

    [SkippableFact]
    public async Task Reconciler_completes_an_operation_whose_response_was_lost()
    {
        RequireStorage(_fixture);
        var key = "send-reconcile-" + Guid.NewGuid().ToString("N");
        var body = new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "text", data = new { text = "reconcile me" } } },
            nyarugram_idempotency_key = key,
        };

        _fixture.Backend.DropOperationResponse(P.RpcRequest.OperationOneofCase.SendMessage);
        var failed = await _fixture.CallAsync("send_private_message", body);
        Assert.Equal(-500, failed.GetProperty("retcode").GetInt32());

        var context = _fixture.Runtime.Require();
        var resolved = await _fixture.Get<SendService>().ReconcileAsync(context, CancellationToken.None);
        Assert.True(resolved >= 1);

        var record = await _fixture.Get<IdempotencyStore>()
            .GetAsync(context.Account.AccountId, SendService.Scope, key, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(IdempotencyStates.Completed, record!.State);
    }

    // ---------------------------------------------------------------- finding 3

    [SkippableFact]
    public async Task Encrypted_message_is_recorded_as_skipped_and_does_not_block_the_page()
    {
        RequireStorage(_fixture);
        var message = _fixture.Backend.InjectEncryptedMessage();
        await _fixture.SyncAsync();

        var reasons = await _fixture.QueryAsync(
            "SELECT reason FROM nyarugram_milky.message_skip WHERE account_id=$1 AND message_id=$2",
            _fixture.AccountId, message.MessageId);
        Assert.Single(reasons);
        Assert.Contains("e2ee", (string)reasons[0]!);

        var cursor = await _fixture.Get<AccountStore>().GetCursorAsync(_fixture.AccountId, CancellationToken.None);
        Assert.True(cursor.Pts > 0, "the page advanced past a permanently unsupported message");
    }

    [SkippableFact]
    public async Task Transient_backend_failure_does_not_advance_the_cursor()
    {
        RequireStorage(_fixture);
        var before = await _fixture.Get<AccountStore>().GetCursorAsync(_fixture.AccountId, CancellationToken.None);
        _fixture.Backend.InjectFriendMessage("retry me");

        _fixture.Backend.FailNextRpc(500, "BOTGRAM_STORAGE_FAILURE");
        await Assert.ThrowsAsync<BackendException>(() => _fixture.SyncAsync());

        var afterFailure = await _fixture.Get<AccountStore>().GetCursorAsync(_fixture.AccountId, CancellationToken.None);
        Assert.Equal(before.Pts, afterFailure.Pts);

        await _fixture.SyncAsync();
        var afterRetry = await _fixture.Get<AccountStore>().GetCursorAsync(_fixture.AccountId, CancellationToken.None);
        Assert.True(afterRetry.Pts > before.Pts);
    }

    // ---------------------------------------------------------------- finding 4

    [SkippableFact]
    public async Task Events_are_not_retired_when_no_consumer_is_connected()
    {
        RequireStorage(_fixture);
        _fixture.Backend.InjectFriendMessage("no consumer");
        await _fixture.SyncAsync();
        await Task.Delay(700);

        var metrics = await _fixture.Get<OutboxStore>()
            .GetMetricsAsync(_fixture.AccountId, CancellationToken.None);
        Assert.True(metrics.Pending > 0, "events were retired as delivered without any consumer");
    }

    [SkippableFact]
    public async Task Consumer_replays_events_missed_while_disconnected()
    {
        RequireStorage(_fixture);
        var consumer = "c-" + Guid.NewGuid().ToString("N");
        var outbox = _fixture.Get<OutboxStore>();
        var before = await outbox.LatestSeqAsync(_fixture.AccountId, CancellationToken.None);

        await using (var first = await ConnectSseAsync(consumer, cursor: null))
        {
            _fixture.Backend.InjectFriendMessage("live one");
            await _fixture.SyncAsync();
            var live = await ReadEventAsync(first.Reader, TimeSpan.FromSeconds(15));
            Assert.Equal("message_receive", live.GetProperty("event_type").GetString());
        }

        // Wait until the server advanced the durable cursor past the pre-connection head, i.e.
        // past "live one". A first connection registers the consumer at its start position;
        // only the sent-and-acked position proves the closed connection was fully persisted.
        await WaitUntilAsync(async () =>
            await outbox.GetConsumerCursorAsync(
                _fixture.AccountId, consumer, CancellationToken.None) > before);

        // While disconnected another event is produced.
        _fixture.Backend.InjectFriendMessage("missed while away");
        await _fixture.SyncAsync();
        await Task.Delay(600);

        await using var second = await ConnectSseAsync(consumer, cursor: null);
        var replayed = await ReadEventAsync(second.Reader, TimeSpan.FromSeconds(15));
        Assert.Equal("message_receive", replayed.GetProperty("event_type").GetString());
        var text = replayed.GetProperty("data").GetProperty("segments").EnumerateArray().First()
            .GetProperty("data").GetProperty("text").GetString();
        Assert.Equal("missed while away", text);
    }

    [SkippableFact]
    public async Task Health_reports_pending_and_expired_delivery_state()
    {
        RequireStorage(_fixture);
        var response = await _fixture.Client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.TryGetProperty("outbox_pending", out _));
        Assert.True(json.TryGetProperty("outbox_expired", out _));
        Assert.True(json.TryGetProperty("event_consumers", out _));
    }

    // ------------------------------------------------------------ finding 5/6/9

    [SkippableFact]
    public async Task Outgoing_call_confirms_the_key_and_reaches_media_ready()
    {
        RequireStorage(_fixture);
        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = "call-" + Guid.NewGuid().ToString("N"),
            video = false,
        });
        var callId = started.GetProperty("call").GetProperty("call_id").GetString();
        var backendCallId = _fixture.Backend.LatestCallId;

        await _fixture.SyncAsync();                       // ringing
        var accepted = _fixture.Backend.GetCall(backendCallId);
        Assert.Equal("ringing", accepted.State);

        _fixture.Backend.AdvanceCall(backendCallId, "accepted");
        await _fixture.SyncAsync();                       // confirm_call + negotiating

        var confirmed = _fixture.Backend.GetCall(backendCallId);
        Assert.Equal("active", confirmed.State);
        Assert.Equal(32, confirmed.GaHash.Length);
        Assert.True(confirmed.Fingerprint != 0, "the adapter never confirmed the call key");

        await _fixture.SyncAsync();                       // active -> media
        await _fixture.WaitForMediaStageAsync(Guid.Parse(callId!), MediaStages.Ready, TimeSpan.FromSeconds(30));
        var info = await _fixture.CallOkAsync("nyarugram_get_call", new { call_id = callId });
        var call = info.GetProperty("call");
        Assert.Equal("active", call.GetProperty("state").GetString());
        Assert.Equal("ready", call.GetProperty("media_state").GetString());

        var ended = await _fixture.CallOkAsync("nyarugram_end_call", new
        {
            call_id = callId,
            reason = "hangup",
        });
        Assert.Equal("ended", ended.GetProperty("call").GetProperty("state").GetString());
    }

    [SkippableFact]
    public async Task Call_start_is_idempotent_and_rejects_a_different_payload()
    {
        RequireStorage(_fixture);
        var key = "call-idem-" + Guid.NewGuid().ToString("N");
        var before = _fixture.Backend.CallCount;

        var first = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = key,
            video = false,
        });
        var second = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = key,
            video = false,
        });

        Assert.Equal(
            first.GetProperty("call").GetProperty("call_id").GetString(),
            second.GetProperty("call").GetProperty("call_id").GetString());
        Assert.Equal(before + 1, _fixture.Backend.CallCount);

        // Same key, different callee.
        var conflict = await _fixture.CallAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.BobUin,
            request_id = key,
            video = false,
        });
        Assert.Equal(-409, conflict.GetProperty("retcode").GetInt32());
    }

    [SkippableFact]
    public async Task Hangup_with_unknown_backend_result_is_not_reported_as_released()
    {
        RequireStorage(_fixture);
        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = "call-hang-" + Guid.NewGuid().ToString("N"),
            video = false,
        });
        var callId = started.GetProperty("call").GetProperty("call_id").GetString();

        _fixture.Backend.FailNextRpc(500, "BOTGRAM_STORAGE_FAILURE");
        var failed = await _fixture.CallAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal(-500, failed.GetProperty("retcode").GetInt32());

        var afterFailure = await _fixture.CallOkAsync("nyarugram_get_call", new { call_id = callId });
        Assert.NotEqual("ended", afterFailure.GetProperty("call").GetProperty("state").GetString());

        var retried = await _fixture.CallOkAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });
        Assert.Equal("ended", retried.GetProperty("call").GetProperty("state").GetString());
    }

    [SkippableFact]
    public async Task Restart_releases_calls_left_by_a_previous_process()
    {
        RequireStorage(_fixture);
        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = "call-restart-" + Guid.NewGuid().ToString("N"),
            video = false,
        });
        var callId = Guid.Parse(started.GetProperty("call").GetProperty("call_id").GetString()!);
        var backendCallId = _fixture.Backend.LatestCallId;

        // Simulate a previous process owning this call.
        await _fixture.ExecuteAsync(
            "UPDATE nyarugram_milky.call SET instance_id = 'previous-process' WHERE call_id = $1", callId);

        await _fixture.Get<CallService>().ReconcileAsync(_fixture.Runtime.Require(), CancellationToken.None);

        var after = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, callId, CancellationToken.None);
        Assert.NotNull(after);
        Assert.Equal(CallStates.Ended, after!.State);
        Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
    }

    [SkippableFact]
    public async Task Call_state_and_media_events_have_distinct_stable_ids()
    {
        RequireStorage(_fixture);
        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = "call-events-" + Guid.NewGuid().ToString("N"),
            video = false,
        });
        var callId = started.GetProperty("call").GetProperty("call_id").GetString();
        var backendCallId = _fixture.Backend.LatestCallId;

        await _fixture.SyncAsync();
        _fixture.Backend.AdvanceCall(backendCallId, "accepted");
        await _fixture.SyncAsync();
        await _fixture.SyncAsync();
        await _fixture.CallOkAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });

        var ids = await _fixture.QueryAsync(
            "SELECT event_id FROM nyarugram_milky.outbox WHERE account_id=$1 AND event_type LIKE 'nyarugram_call%'",
            _fixture.AccountId);
        var strings = ids.Cast<string>().ToList();
        Assert.NotEmpty(strings);
        Assert.Equal(strings.Count, strings.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(strings, id => id.Contains(":state:", StringComparison.Ordinal));
        Assert.Contains(strings, id => id.Contains(":media:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private const int MilkyRetCodeNotLoggedIn = -403;

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int attempts = 50)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("condition was not met in time");
    }

    private static async Task<(HttpStatusCode StatusCode, byte[] Body)> PostProtoAsync(
        HttpClient client, string path, P.RpcRequest request)
    {
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = new(BackendClient.ProtobufMediaType);
        using var response = await client.PostAsync(path, content);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
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

    private async Task<SseConnection> ConnectSseAsync(string consumer, long? cursor)
    {
        var query = $"?nyarugram_consumer={consumer}" + (cursor is { } value ? $"&nyarugram_cursor={value}" : "");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/event" + query);
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
        throw new TimeoutException("no Milky event received");
    }
}
