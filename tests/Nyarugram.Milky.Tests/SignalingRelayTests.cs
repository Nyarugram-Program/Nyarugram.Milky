using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Core;
using Xunit;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Sixth-round review regressions for outbound signaling delivery: bounded capacity,
/// ordering, transient retry, lost responses, expiry, retry exhaustion and stopping
/// an obsolete queue.
/// </summary>
public sealed class SignalingRelayTests
{
    private static RuntimeOptions Options(
        int capacity = 16, int attempts = 5, int delayMs = 10, int maxAgeMs = 60_000)
        => new()
        {
            SignalingQueueCapacity = capacity,
            SignalingMaxAttempts = attempts,
            SignalingRetryBaseDelay = TimeSpan.FromMilliseconds(delayMs),
            SignalingMaxAge = TimeSpan.FromMilliseconds(maxAgeMs),
        };

    private static SignalingRelay Relay(
        Func<byte[], CancellationToken, Task> send,
        RuntimeOptions options,
        Action<string> onFailure,
        Func<bool>? isCurrent = null,
        Func<TimeSpan>? clock = null)
        => new(send, options, onFailure, isCurrent ?? (() => true), NullLogger.Instance, clock);

    private static byte[] Payload(string value) => Encoding.UTF8.GetBytes(value);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException("condition was not met before the timeout");
    }

    [Fact]
    public async Task Signals_are_delivered_in_order_and_transient_failures_are_retried()
    {
        var sent = new ConcurrentQueue<string>();
        var attempts = new ConcurrentDictionary<string, int>();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var relay = Relay((data, _) =>
        {
            var text = Encoding.UTF8.GetString(data);
            sent.Enqueue(text);
            var attempt = attempts.AddOrUpdate(text, 1, (_, value) => value + 1);
            if (text == "a" && attempt == 1)
            {
                // A result the adapter never observed: the retry must reuse the payload.
                throw new BackendException("transport_timeout", 0, false) { OutcomeUnknown = true };
            }
            return Task.CompletedTask;
        }, Options(), reason => failure.TrySetResult(reason));

        Assert.True(relay.Enqueue(Payload("a")));
        Assert.True(relay.Enqueue(Payload("b")));
        Assert.True(relay.Enqueue(Payload("c")));

        await WaitUntilAsync(() => sent.Count >= 4, TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(["a", "a", "b", "c"], sent.ToArray());
        Assert.False(failure.Task.IsCompleted, "a recoverable signal must not fail the call");
    }

    [Fact]
    public async Task A_permanent_failure_stops_the_queue_and_reports_once()
    {
        var sent = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay((data, _) =>
        {
            sent.Enqueue(Encoding.UTF8.GetString(data));
            throw new BackendException("BOTGRAM_RPC_INVALID_ARGUMENT", 400, false);
        }, Options(), reason => failures.Enqueue(reason));

        relay.Enqueue(Payload("a"));
        relay.Enqueue(Payload("b"));
        await WaitUntilAsync(() => failures.Count == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(["signaling_delivery_failed"], failures.ToArray());
        Assert.Single(sent);
        Assert.False(relay.Enqueue(Payload("c")));
    }

    [Fact]
    public async Task Retry_exhaustion_reports_a_single_failure()
    {
        var sends = 0;
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay((_, _) =>
        {
            Interlocked.Increment(ref sends);
            throw new BackendException("BOTGRAM_STORAGE_FAILURE", 503, true);
        }, Options(attempts: 2, delayMs: 10), reason => failures.Enqueue(reason));

        relay.Enqueue(Payload("a"));
        await WaitUntilAsync(() => failures.Count == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(["signaling_retry_exhausted"], failures.ToArray());
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task Queue_overflow_reports_a_single_failure_and_refuses_more_signals()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new SemaphoreSlim(0, 1);
        var sends = 0;
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay(async (_, ct) =>
        {
            Interlocked.Increment(ref sends);
            started.TrySetResult();
            await release.WaitAsync(ct);
        }, Options(capacity: 1), reason => failures.Enqueue(reason));

        Assert.True(relay.Enqueue(Payload("a")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(relay.Enqueue(Payload("b")));   // occupies the only queue slot
        Assert.False(relay.Enqueue(Payload("c")));  // overflow
        Assert.False(relay.Enqueue(Payload("d")));

        await WaitUntilAsync(() => failures.Count == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(["signaling_queue_overflow"], failures.ToArray());
        Assert.Equal(1, sends);
        release.Release();
    }

    [Fact]
    public async Task A_signal_that_waited_too_long_is_expired_instead_of_delivered()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new SemaphoreSlim(0, 1);
        var sent = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay(async (data, ct) =>
        {
            var text = Encoding.UTF8.GetString(data);
            sent.Enqueue(text);
            if (text == "a")
            {
                started.TrySetResult();
                await release.WaitAsync(ct);
            }
        }, Options(capacity: 2, maxAgeMs: 100), reason => failures.Enqueue(reason));

        relay.Enqueue(Payload("a"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        relay.Enqueue(Payload("b"));
        await Task.Delay(250);
        release.Release();

        await WaitUntilAsync(() => failures.Count == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(["signaling_expired"], failures.ToArray());
        Assert.Equal(["a"], sent.ToArray());
    }

    [Fact]
    public async Task Disposing_the_queue_cancels_retries_and_drops_pending_signals()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var failures = new ConcurrentQueue<string>();
        var relay = Relay(async (_, ct) =>
        {
            Interlocked.Increment(ref sends);
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }, Options(), reason => failures.Enqueue(reason));

        relay.Enqueue(Payload("a"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        relay.Enqueue(Payload("b"));
        await relay.DisposeAsync();
        await Task.Delay(150);

        Assert.Equal(1, sends);
        Assert.Empty(failures);
        Assert.False(relay.Enqueue(Payload("c")));
    }

    [Fact]
    public async Task A_replaced_instance_stops_delivering_and_never_resumes()
    {
        var current = false;
        var sent = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay((data, _) =>
        {
            sent.Enqueue(Encoding.UTF8.GetString(data));
            return Task.CompletedTask;
        }, Options(), reason => failures.Enqueue(reason), () => current);

        relay.Enqueue(Payload("old"));
        await Task.Delay(150);
        Assert.Empty(sent);

        // Once the captured binding is no longer current the worker exits; the queue can
        // never resume for a newer media instance, which gets its own relay.
        current = true;
        relay.Enqueue(Payload("new"));
        await Task.Delay(300);

        Assert.Empty(sent);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task An_entry_that_expires_in_the_queue_is_not_sent()
    {
        var clock = TimeSpan.Zero;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new SemaphoreSlim(0, 1);
        var sent = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay(async (data, ct) =>
        {
            var text = Encoding.UTF8.GetString(data);
            sent.Enqueue(text);
            if (text == "block")
            {
                started.TrySetResult();
                await release.WaitAsync(ct);
            }
        }, Options(capacity: 2, maxAgeMs: 50), reason => failures.Enqueue(reason),
            clock: () => clock);

        relay.Enqueue(Payload("block"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        relay.Enqueue(Payload("expired"));
        clock = TimeSpan.FromMilliseconds(100);
        release.Release();

        await WaitUntilAsync(() => failures.Count == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Equal(["signaling_expired"], failures.ToArray());
        Assert.Equal(["block"], sent.ToArray());
    }

    [Fact]
    public async Task Expiry_during_retry_wait_does_not_send_a_second_time()
    {
        var sent = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay((data, _) =>
        {
            sent.Enqueue(Encoding.UTF8.GetString(data));
            throw new BackendException("BOTGRAM_STORAGE_FAILURE", 503, true);
        }, Options(attempts: 5, delayMs: 100, maxAgeMs: 20), reason => failures.Enqueue(reason));

        relay.Enqueue(Payload("a"));
        await WaitUntilAsync(() => failures.Count == 1, TimeSpan.FromSeconds(3));
        await Task.Delay(50);

        Assert.Equal(["signaling_expired"], failures.ToArray());
        Assert.Single(sent, "a");
    }

    [Fact]
    public async Task An_in_flight_send_is_bounded_by_the_remaining_lifetime()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay(async (data, ct) =>
        {
            sent.Enqueue(Encoding.UTF8.GetString(data));
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }, Options(maxAgeMs: 30), reason => failures.Enqueue(reason));

        relay.Enqueue(Payload("a"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => failures.Count == 1, TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.Equal(["signaling_expired"], failures.ToArray());
        Assert.Single(sent);
    }

    [Fact]
    public async Task A_valid_retry_within_the_lifetime_keeps_order_and_payload()
    {
        var sent = new ConcurrentQueue<string>();
        var attempts = new ConcurrentDictionary<string, int>();
        var failures = new ConcurrentQueue<string>();
        await using var relay = Relay((data, _) =>
        {
            var text = Encoding.UTF8.GetString(data);
            sent.Enqueue(text);
            var attempt = attempts.AddOrUpdate(text, 1, (_, value) => value + 1);
            if (text == "a" && attempt == 1)
                throw new BackendException("BOTGRAM_STORAGE_FAILURE", 503, true);
            return Task.CompletedTask;
        }, Options(delayMs: 10, maxAgeMs: 5_000), reason => failures.Enqueue(reason));

        relay.Enqueue(Payload("a"));
        relay.Enqueue(Payload("b"));
        relay.Enqueue(Payload("c"));

        await WaitUntilAsync(() => sent.Count >= 4, TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(["a", "a", "b", "c"], sent.ToArray());
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Disposing_during_an_expiry_retry_does_not_report_a_failure()
    {
        var failures = new ConcurrentQueue<string>();
        var relay = Relay(async (data, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }, Options(delayMs: 100, maxAgeMs: 200), reason => failures.Enqueue(reason));

        relay.Enqueue(Payload("a"));
        await Task.Delay(20);
        await relay.DisposeAsync();
        await Task.Delay(50);

        Assert.Empty(failures);
    }
}
