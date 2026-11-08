using System.Text;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Media;
using Nyarugram.Milky.Storage;
using Xunit;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Sixth-round review regressions for media runtime failures: an agent that dies after
/// readiness must end the call, emit media_closed, and free the backend line; a late
/// fault from a replaced instance must not touch the new media.
/// </summary>
[Collection("adapter")]
public sealed class Round6RegressionTests
{
    private readonly AdapterFixture _fixture;

    public Round6RegressionTests(AdapterFixture fixture) => _fixture = fixture;

    private static void RequireStorage(AdapterFixture fixture)
    {
        if (fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {fixture.StartupError.Message}");
    }

    [SkippableFact]
    public async Task Media_failure_after_ready_fails_the_call_and_emits_media_closed()
    {
        RequireStorage(_fixture);
        var (callId, backendCallId) = await StartReadyCallAsync("fault-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);

        var engine = _fixture.Get<LoopbackMediaEngine>();
        Assert.True(engine.FailSession(id, "simulated agent crash"));

        var failed = await WaitForStateAsync(id, CallStates.Failed, TimeSpan.FromSeconds(20));
        Assert.Equal("closed", failed.MediaState);
        Assert.Equal("closed", failed.MediaStage);
        Assert.Equal("media_agent_failed", failed.EndReason);
        Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);

        var closedEvents = await ClosedEventCountAsync(callId);
        Assert.True(closedEvents >= 1, "no media_closed event was emitted for the dead agent");

        // The adapter must still be able to start a fresh call after the failure.
        await StartReadyCallAsync("redial-" + Guid.NewGuid().ToString("N"));
    }

    [SkippableFact]
    public async Task A_late_fault_from_a_replaced_media_instance_is_ignored()
    {
        RequireStorage(_fixture);
        var (callId, _) = await StartReadyCallAsync("stale-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var engine = _fixture.Get<LoopbackMediaEngine>();

        var old = engine.Find(id);
        Assert.NotNull(old);

        // Replace the instance exactly like a media restart would, then let the old agent
        // report its exit late.
        await engine.StartAsync(
            new MediaSessionRequest(
                id, FakeBackend.FriendUin, false, true, new byte[256],
                Array.Empty<MediaEndpoint>(), "", false, null),
            _ => Task.CompletedTask, CancellationToken.None);
        Assert.True(((LoopbackSession)old!).Fail("late crash from a replaced instance"));

        await Task.Delay(1500);
        var current = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, id, CancellationToken.None);
        Assert.Equal(CallStates.Active, current!.State);
        Assert.Equal("ready", current.MediaState);

        await _fixture.CallOkAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });
        var ended = await WaitForTerminalAsync(id, TimeSpan.FromSeconds(20));
        Assert.Equal(CallStates.Ended, ended.State);
        Assert.Equal(1L, await ClosedEventCountAsync(callId));
    }

    [SkippableFact]
    public async Task Media_fault_and_hangup_race_produces_one_terminal_media_closed()
    {
        RequireStorage(_fixture);
        var (callId, _) = await StartReadyCallAsync("race-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);

        var end = _fixture.CallOkAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });
        Assert.True(_fixture.Get<LoopbackMediaEngine>().FailSession(id, "crash during hangup"));
        await end;

        var final = await WaitForTerminalAsync(id, TimeSpan.FromSeconds(20));
        Assert.True(final.State is CallStates.Ended or CallStates.Failed);
        Assert.Equal("closed", final.MediaState);
        Assert.Equal(1L, await ClosedEventCountAsync(callId));
    }

    [SkippableFact]
    public async Task A_media_fault_with_a_failed_discard_is_reconciled_and_the_line_freed()
    {
        RequireStorage(_fixture);
        var (callId, backendCallId) = await StartReadyCallAsync("reconcile-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);

        // The first release attempt fails after the media fault; the maintenance worker
        // must confirm it later without any manual retry.
        _fixture.Backend.FailOperation(
            P.RpcRequest.OperationOneofCase.DiscardCall, 503, "BOTGRAM_STORAGE_FAILURE");
        Assert.True(_fixture.Get<LoopbackMediaEngine>().FailSession(id, "simulated agent crash"));

        var failed = await WaitForStateAsync(id, CallStates.Failed, TimeSpan.FromSeconds(20));
        Assert.Equal("pending", failed.ReconcileState);
        Assert.NotNull(failed.PendingEndReason);
        Assert.NotEqual("ended", _fixture.Backend.GetCall(backendCallId).State);

        await _fixture.ExecuteAsync(
            "UPDATE nyarugram_milky.call SET reconcile_next_at = now() - interval '1 second' WHERE call_id = $1",
            id);
        await _fixture.MaintainCallsAsync();

        var after = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, id, CancellationToken.None);
        Assert.Equal(CallStates.Failed, after!.State);
        Assert.Null(after.PendingEndReason);
        Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
    }

    [SkippableFact]
    public async Task A_transient_signaling_failure_is_retried_in_order_without_failing_the_call()
    {
        RequireStorage(_fixture);
        var (callId, _) = await StartReadyCallAsync("signal-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var engine = _fixture.Get<LoopbackMediaEngine>();
        var run = "sig-" + Guid.NewGuid().ToString("N");

        // The first send returns 500 before committing; the relay retries it and then
        // keeps the remaining signals in order. A before-commit failure leaves no trace.
        _fixture.Backend.FailOperation(
            P.RpcRequest.OperationOneofCase.SendSignalingData, 500, "BOTGRAM_STORAGE_FAILURE");
        await engine.EmitSignalingAsync(id, System.Text.Encoding.UTF8.GetBytes($"{run}-a"));
        await engine.EmitSignalingAsync(id, System.Text.Encoding.UTF8.GetBytes($"{run}-b"));
        await engine.EmitSignalingAsync(id, System.Text.Encoding.UTF8.GetBytes($"{run}-c"));

        await WaitUntilAsync(
            () => _fixture.Backend.SignalingPayloads.Count(value => value.StartsWith(run, StringComparison.Ordinal)) >= 3,
            TimeSpan.FromSeconds(10));
        await Task.Delay(150);

        var delivered = _fixture.Backend.SignalingPayloads
            .Where(value => value.StartsWith(run, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal([$"{run}-a", $"{run}-b", $"{run}-c"], delivered);

        var current = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, id, CancellationToken.None);
        Assert.Equal(CallStates.Active, current!.State);
        Assert.Equal("ready", current.MediaState);

        await _fixture.CallOkAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });
    }

    [SkippableFact]
    public async Task A_lost_signaling_response_is_retried_with_the_same_payload()
    {
        RequireStorage(_fixture);
        var (callId, _) = await StartReadyCallAsync("signal-lost-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var engine = _fixture.Get<LoopbackMediaEngine>();
        var run = "lost-" + Guid.NewGuid().ToString("N");

        // The backend commits the signal and then drops the connection, so the adapter
        // cannot know it was applied and must retry the exact payload.
        _fixture.Backend.DropOperationResponse(P.RpcRequest.OperationOneofCase.SendSignalingData);
        await engine.EmitSignalingAsync(id, System.Text.Encoding.UTF8.GetBytes($"{run}-a"));
        await engine.EmitSignalingAsync(id, System.Text.Encoding.UTF8.GetBytes($"{run}-b"));
        await engine.EmitSignalingAsync(id, System.Text.Encoding.UTF8.GetBytes($"{run}-c"));

        await WaitUntilAsync(
            () => _fixture.Backend.SignalingPayloads.Count(value => value.StartsWith(run, StringComparison.Ordinal)) >= 4,
            TimeSpan.FromSeconds(10));
        await Task.Delay(150);

        var delivered = _fixture.Backend.SignalingPayloads
            .Where(value => value.StartsWith(run, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal([$"{run}-a", $"{run}-a", $"{run}-b", $"{run}-c"], delivered);

        var current = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, id, CancellationToken.None);
        Assert.Equal(CallStates.Active, current!.State);

        await _fixture.CallOkAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });
    }

    [SkippableFact]
    public async Task Signaling_retry_exhaustion_fails_the_call_and_releases_the_line()
    {
        RequireStorage(_fixture);
        var (callId, backendCallId) = await StartReadyCallAsync("sigfail-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var engine = _fixture.Get<LoopbackMediaEngine>();
        var run = "sigfail-" + Guid.NewGuid().ToString("N");

        try
        {
            _fixture.Backend.FailOperationTimes(
                P.RpcRequest.OperationOneofCase.SendSignalingData, 500, "BOTGRAM_STORAGE_FAILURE", 100);
            await engine.EmitSignalingAsync(id, System.Text.Encoding.UTF8.GetBytes(run));

            var failed = await WaitForStateAsync(id, CallStates.Failed, TimeSpan.FromSeconds(20));
            Assert.Equal("closed", failed.MediaState);
            Assert.Equal("closed", failed.MediaStage);
            Assert.Equal("signaling_retry_exhausted", failed.EndReason);
            Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
            Assert.True(await ClosedEventCountAsync(callId) >= 1);
        }
        finally
        {
            _fixture.Backend.ClearRepeatedFault();
        }
    }

    [SkippableFact]
    public async Task A_replaced_media_instance_cannot_send_signals_for_the_call()
    {
        RequireStorage(_fixture);
        var (callId, _) = await StartReadyCallAsync("sigstale-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var engine = _fixture.Get<LoopbackMediaEngine>();
        var run = "sigstale-" + Guid.NewGuid().ToString("N");

        var old = engine.Find(id);
        Assert.NotNull(old);
        await engine.StartAsync(
            new MediaSessionRequest(
                id, FakeBackend.FriendUin, false, true, new byte[256],
                Array.Empty<MediaEndpoint>(), "", false, null),
            _ => Task.CompletedTask, CancellationToken.None);

        await ((LoopbackSession)old!).EmitSignalingAsync(
            System.Text.Encoding.UTF8.GetBytes($"{run}-old"));
        await Task.Delay(500);
        Assert.DoesNotContain(_fixture.Backend.SignalingPayloads,
            value => value.StartsWith(run, StringComparison.Ordinal));

        await _fixture.CallOkAsync("nyarugram_end_call", new { call_id = callId, reason = "hangup" });
    }

    [SkippableFact]
    public async Task Media_fault_during_a_database_outage_is_retried_without_manual_action()
    {
        RequireStorage(_fixture);
        var (callId, backendCallId) = await StartReadyCallAsync("dbfault-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var service = _fixture.Get<CallService>();
        var engine = _fixture.Get<LoopbackMediaEngine>();

        try
        {
            service.FinalizationFaultForTests =
                _ => new Npgsql.NpgsqlException("simulated database outage");
            Assert.True(engine.FailSession(id, "simulated agent crash"));

            await WaitUntilAsync(() => service.PendingFaultCountForTests == 1, TimeSpan.FromSeconds(10));
            var during = await _fixture.Get<CallStore>()
                .GetAsync(_fixture.AccountId, id, CancellationToken.None);
            Assert.Equal(CallStates.Active, during!.State);
            Assert.Equal("ready", during.MediaState);
            Assert.False(engine.Find(id) is { State: "active" }, "media must be stopped during the outage");

            service.FinalizationFaultForTests = null;
            var failed = await WaitForStateAsync(id, CallStates.Failed, TimeSpan.FromSeconds(20));
            Assert.Equal("closed", failed.MediaState);
            Assert.Equal("closed", failed.MediaStage);
            Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
            Assert.Equal(1L, await ClosedEventCountAsync(callId));
        }
        finally
        {
            service.FinalizationFaultForTests = null;
        }
    }

    [SkippableFact]
    public async Task Signaling_failure_during_a_database_outage_is_retried_without_manual_action()
    {
        RequireStorage(_fixture);
        var (callId, backendCallId) = await StartReadyCallAsync("dbsig-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var service = _fixture.Get<CallService>();
        var binding = service.SignalingBindingForTests(id);
        Assert.NotNull(binding?.Relay);

        try
        {
            service.FinalizationFaultForTests =
                _ => new Npgsql.NpgsqlException("simulated database outage");
            _fixture.Backend.FailOperationTimes(
                P.RpcRequest.OperationOneofCase.SendSignalingData, 500, "BOTGRAM_STORAGE_FAILURE", 100);
            Assert.True(binding!.Relay!.Enqueue(Encoding.UTF8.GetBytes("db-signal")));

            await WaitUntilAsync(() => service.PendingFaultCountForTests == 1, TimeSpan.FromSeconds(20));
            var during = await _fixture.Get<CallStore>()
                .GetAsync(_fixture.AccountId, id, CancellationToken.None);
            Assert.Equal(CallStates.Active, during!.State);
            Assert.Equal("ready", during.MediaState);

            service.FinalizationFaultForTests = null;
            _fixture.Backend.ClearRepeatedFault();
            var failed = await WaitForStateAsync(id, CallStates.Failed, TimeSpan.FromSeconds(20));
            Assert.Equal("closed", failed.MediaState);
            Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
            Assert.True(await ClosedEventCountAsync(callId) >= 1);
        }
        finally
        {
            service.FinalizationFaultForTests = null;
            _fixture.Backend.ClearRepeatedFault();
        }
    }

    [SkippableFact]
    public async Task Credential_revocation_failure_after_the_terminal_commit_is_idempotently_retried()
    {
        RequireStorage(_fixture);
        var (callId, backendCallId) = await StartReadyCallAsync("dbrevoke-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var service = _fixture.Get<CallService>();

        var opened = await _fixture.CallOkAsync("nyarugram_open_call_media", new
        {
            call_id = callId,
            input = true,
            output = true,
        });
        Assert.NotEmpty(opened.GetProperty("token").GetString()!);

        try
        {
            service.FinalizationFaultForTests = step =>
                step == "revoke_tokens" ? new Npgsql.NpgsqlException("simulated revocation failure") : null;
            Assert.True(_fixture.Get<LoopbackMediaEngine>().FailSession(id, "simulated agent crash"));

            var failed = await WaitForStateAsync(id, CallStates.Failed, TimeSpan.FromSeconds(20));
            Assert.Equal("closed", failed.MediaState);
            Assert.True(await ClosedEventCountAsync(callId) >= 1);
            Assert.Equal(1L, await _fixture.ScalarAsync<long>(
                "SELECT count(*) FROM nyarugram_milky.call_media_token WHERE call_id=$1", id));
            await WaitUntilAsync(() => service.PendingFaultCountForTests == 1, TimeSpan.FromSeconds(10));

            service.FinalizationFaultForTests = null;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            long tokens = 1;
            while (tokens != 0 && DateTimeOffset.UtcNow < deadline)
            {
                tokens = await _fixture.ScalarAsync<long>(
                    "SELECT count(*) FROM nyarugram_milky.call_media_token WHERE call_id=$1", id);
                if (tokens != 0) await Task.Delay(200);
            }
            Assert.Equal(0L, tokens);
            Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
            Assert.Equal(1L, await ClosedEventCountAsync(callId));
            Assert.Equal(CallStates.Failed, (await _fixture.Get<CallStore>()
                .GetAsync(_fixture.AccountId, id, CancellationToken.None))!.State);
        }
        finally
        {
            service.FinalizationFaultForTests = null;
        }
    }

    [SkippableFact]
    public async Task A_stale_signaling_failure_callback_cannot_close_replaced_media()
    {
        RequireStorage(_fixture);
        var (callId, _) = await StartReadyCallAsync("stalebind-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var service = _fixture.Get<CallService>();
        var engine = _fixture.Get<LoopbackMediaEngine>();
        var call = await _fixture.Get<CallStore>()
            .GetAsync(_fixture.AccountId, id, CancellationToken.None);
        Assert.NotNull(call?.BackendCallId);

        var binding1 = service.SignalingBindingForTests(id);
        Assert.NotNull(binding1?.Relay);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeSignalingFailureLockForTests = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        try
        {
            _fixture.Backend.FailOperationTimes(
                P.RpcRequest.OperationOneofCase.SendSignalingData, 500, "BOTGRAM_STORAGE_FAILURE", 100);
            Assert.True(binding1!.Relay!.Enqueue(Encoding.UTF8.GetBytes("stale-binding")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // The old callback is waiting for the call lock. Install a replacement media
            // instance and binding before releasing it.
            var old = engine.Find(id);
            Assert.NotNull(old);
            var replacement = await engine.StartAsync(
                new MediaSessionRequest(
                    id, FakeBackend.FriendUin, false, true, new byte[256],
                    Array.Empty<MediaEndpoint>(), "", false, null),
                _ => Task.CompletedTask, CancellationToken.None);
            await service.InstallReplacementSignalingForTestsAsync(
                _fixture.Runtime.Require(), call!, call!.BackendCallId!.Value, replacement);

            service.BeforeSignalingFailureLockForTests = null;
            release.TrySetResult();
            await Task.Delay(800);

            var current = await _fixture.Get<CallStore>()
                .GetAsync(_fixture.AccountId, id, CancellationToken.None);
            Assert.Equal(CallStates.Active, current!.State);
            Assert.Equal("ready", current.MediaState);
            Assert.NotSame(binding1, service.SignalingBindingForTests(id));
            Assert.Equal(0L, await ClosedEventCountAsync(callId));

            // A failure on the current binding must still end the call.
            _fixture.Backend.ClearRepeatedFault();
            var binding2 = service.SignalingBindingForTests(id);
            Assert.NotNull(binding2?.Relay);
            _fixture.Backend.FailOperationTimes(
                P.RpcRequest.OperationOneofCase.SendSignalingData, 500, "BOTGRAM_STORAGE_FAILURE", 100);
            Assert.True(binding2!.Relay!.Enqueue(Encoding.UTF8.GetBytes("current-binding")));

            var failed = await WaitForStateAsync(id, CallStates.Failed, TimeSpan.FromSeconds(20));
            Assert.Equal("closed", failed.MediaState);
            Assert.Equal(1L, await ClosedEventCountAsync(callId));
        }
        finally
        {
            service.BeforeSignalingFailureLockForTests = null;
            _fixture.Backend.ClearRepeatedFault();
        }
    }

    [SkippableFact]
    public async Task Pending_media_fault_is_finalized_after_same_account_login()
    {
        RequireStorage(_fixture);
        var (callId, backendCallId) = await StartReadyCallAsync("relogin-fault-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var service = _fixture.Get<CallService>();
        var previous = _fixture.Runtime.Require();
        var opened = await _fixture.CallOkAsync("nyarugram_open_call_media", new
        {
            call_id = callId,
            input = true,
            output = true,
        });
        Assert.NotEmpty(opened.GetProperty("token").GetString()!);
        try
        {
            service.FinalizationFaultForTests = _ => new Npgsql.NpgsqlException("simulated database outage");
            Assert.True(_fixture.Get<LoopbackMediaEngine>().FailSession(id, "simulated agent crash"));
            await WaitUntilAsync(() => service.PendingFaultCountForTests == 1, TimeSpan.FromSeconds(10));

            var current = await _fixture.Get<SessionManager>().LoginAsync("bot", "secret", null, CancellationToken.None);
            Assert.True(current.Generation > previous.Generation);
            await service.ReconcileAsync(current, CancellationToken.None);
            // Let the pending retry become due while persistence is still unavailable.
            await Task.Delay(1200);
            await service.ProcessPendingFaultsForTestsAsync(CancellationToken.None);
            Assert.Equal(1, service.PendingFaultCountForTests);

            service.FinalizationFaultForTests = null;
            await WaitUntilAsync(() => service.PendingFaultCountForTests == 0, TimeSpan.FromSeconds(20));
            // Concurrent maintenance/finalization passes must remain idempotent.
            await Task.WhenAll(
                service.ProcessPendingFaultsForTestsAsync(CancellationToken.None),
                service.MaintainAsync(current, CancellationToken.None));
            var failed = await _fixture.Get<CallStore>().GetAsync(_fixture.AccountId, id, CancellationToken.None);
            Assert.Equal(CallStates.Failed, failed!.State);
            Assert.Equal("closed", failed.MediaState);
            Assert.Equal("media_agent_failed", failed.EndReason);
            Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
            Assert.Equal(1L, await ClosedEventCountAsync(callId));
            Assert.Equal(0L, await _fixture.ScalarAsync<long>(
                "SELECT count(*) FROM nyarugram_milky.call_media_token WHERE call_id=$1", id));
        }
        finally
        {
            service.FinalizationFaultForTests = null;
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_fault_waits_for_its_account_and_server_to_return(bool differentServer)
    {
        RequireStorage(_fixture);
        var (callId, backendCallId) = await StartReadyCallAsync("scope-fault-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse(callId);
        var service = _fixture.Get<CallService>();
        var owner = _fixture.Runtime.Require();
        try
        {
            service.FinalizationFaultForTests = _ => new Npgsql.NpgsqlException("simulated database outage");
            Assert.True(_fixture.Get<LoopbackMediaEngine>().FailSession(id, "simulated agent crash"));
            await WaitUntilAsync(() => service.PendingFaultCountForTests == 1, TimeSpan.FromSeconds(10));
            // Isolate ownership selection from authentication using a synthetic runtime binding.
            var other = differentServer
                ? owner.Account with { ServerDomain = "other.invalid" }
                : owner.Account with { AccountId = Guid.NewGuid(), AccountUuid = Guid.NewGuid().ToString() };
            _fixture.Runtime.Bind(owner with { Account = other, Generation = owner.Generation + 1 });
            service.FinalizationFaultForTests = null;
            await Task.Delay(1200);
            await service.ProcessPendingFaultsForTestsAsync(CancellationToken.None);
            Assert.Equal(1, service.PendingFaultCountForTests);
            var unchanged = await _fixture.Get<CallStore>().GetAsync(owner.Account.AccountId, id, CancellationToken.None);
            Assert.Equal(CallStates.Active, unchanged!.State);
            Assert.NotEqual("ended", _fixture.Backend.GetCall(backendCallId).State);

            _fixture.Runtime.Clear();
            await service.ProcessPendingFaultsForTestsAsync(CancellationToken.None);
            Assert.Equal(1, service.PendingFaultCountForTests);
            await _fixture.Get<SessionManager>().LoginAsync("bot", "secret", null, CancellationToken.None);
            await WaitUntilAsync(() => service.PendingFaultCountForTests == 0, TimeSpan.FromSeconds(20));
            Assert.Equal(CallStates.Failed, (await _fixture.Get<CallStore>()
                .GetAsync(owner.Account.AccountId, id, CancellationToken.None))!.State);
            Assert.Equal("ended", _fixture.Backend.GetCall(backendCallId).State);
            Assert.Equal(1L, await ClosedEventCountAsync(callId));
        }
        finally
        {
            service.FinalizationFaultForTests = null;
            if (_fixture.Runtime.Current?.Account != owner.Account)
                _fixture.Runtime.Bind(owner);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("condition was not met before the timeout");
    }

    private async Task<(string CallId, long BackendCallId)> StartReadyCallAsync(string requestId)
    {
        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = requestId,
            video = false,
        });
        var callId = started.GetProperty("call").GetProperty("call_id").GetString()!;
        var backendCallId = _fixture.Backend.LatestCallId;

        await _fixture.SyncAsync();
        _fixture.Backend.AdvanceCall(backendCallId, "accepted");
        await _fixture.SyncAsync();
        await _fixture.SyncAsync();

        var ready = await _fixture.WaitForMediaStageAsync(
            Guid.Parse(callId), MediaStages.Ready, TimeSpan.FromSeconds(30));
        Assert.Equal("ready", ready.MediaState);
        return (callId, backendCallId);
    }

    private async Task<CallRecord> WaitForStateAsync(Guid callId, string state, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        CallRecord? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await _fixture.Get<CallStore>()
                .GetAsync(_fixture.AccountId, callId, CancellationToken.None);
            if (last is not null && last.State == state) return last;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException(
            $"call {callId} did not reach {state}; last={last?.State}/{last?.MediaState}");
    }

    private async Task<CallRecord> WaitForTerminalAsync(Guid callId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        CallRecord? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await _fixture.Get<CallStore>()
                .GetAsync(_fixture.AccountId, callId, CancellationToken.None);
            if (last is not null && last.IsTerminal) return last;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException(
            $"call {callId} did not become terminal; last={last?.State}/{last?.MediaState}");
    }

    private Task<long> ClosedEventCountAsync(string callId)
        => _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM nyarugram_milky.outbox WHERE account_id=$1 AND event_type=$2 AND payload::text LIKE $3",
            _fixture.AccountId, NyarugramEventType.CallMediaClosed, "%" + callId + "%");
}
