using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Bounded, ordered outbound signaling relay for one media instance.
///
/// Engine signaling is produced on the media read loop, so enqueueing never waits for
/// the backend. One worker sends the queue in order and retries only transient
/// failures. The queue is bound to the captured account generation and call through
/// the caller's <c>isCurrent</c> predicate; after logout, hangup or media replacement
/// the relay is disposed and its pending items are dropped.
///
/// Every send attempt is bounded by the entry's remaining lifetime. Expiry while a
/// request is in flight cancels locally but cannot undo a request the backend may
/// already have committed; delivery is therefore at-least-once for that boundary.
/// </summary>
public sealed class SignalingRelay : IAsyncDisposable
{
    private sealed record Entry(byte[] Data, TimeSpan ExpiresAt);

    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly RuntimeOptions _options;
    private readonly Action<string> _onFailure;
    private readonly Func<bool> _isCurrent;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan> _now;
    private readonly Channel<Entry> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private volatile bool _disposed;
    private int _failurePublished;

    public SignalingRelay(
        Func<byte[], CancellationToken, Task> send,
        RuntimeOptions options,
        Action<string> onFailure,
        Func<bool> isCurrent,
        ILogger logger,
        Func<TimeSpan>? monotonicClock = null)
    {
        _send = send;
        _options = options;
        _onFailure = onFailure;
        _isCurrent = isCurrent;
        _logger = logger;
        // Stopwatch is monotonic and immune to wall-clock adjustments. Tests can
        // supply their own clock to advance expiry without sleeping.
        _now = monotonicClock ?? (() => Stopwatch.GetElapsedTime(0));
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(options.SignalingQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(DrainAsync);
    }

    /// <summary>Queues one engine signal. Returns false when the relay is already closed.</summary>
    public bool Enqueue(byte[] payload)
    {
        if (_disposed || _stop.IsCancellationRequested) return false;
        if (_queue.Writer.TryWrite(new Entry(payload, _now() + _options.SignalingMaxAge))) return true;
        PublishFailure("signaling_queue_overflow");
        return false;
    }

    private async Task DrainAsync()
    {
        var token = _stop.Token;
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                for (var attempt = 1; ; attempt++)
                {
                    // A hangup, logout or media replacement stops the queue. This is
                    // an intentional stop, not a delivery failure: no failure callback.
                    if (!_isCurrent())
                    {
                        _logger.LogDebug("signaling queue stopped: target is no longer current");
                        return;
                    }

                    var remaining = entry.ExpiresAt - _now();
                    if (remaining <= TimeSpan.Zero)
                    {
                        _logger.LogWarning("signaling expired before send (attempt {Attempt})", attempt);
                        PublishFailure("signaling_expired");
                        return;
                    }

                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    try
                    {
                        var sendTask = _send(entry.Data, attemptCts.Token);
                        var timeoutTask = Task.Delay(remaining, timerCts.Token);
                        var winner = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);
                        timerCts.Cancel();
                        if (token.IsCancellationRequested)
                        {
                            attemptCts.Cancel();
                            return;
                        }
                        if (winner == timeoutTask)
                        {
                            // The in-flight request may already have reached the backend;
                            // cancellation only bounds the local wait.
                            attemptCts.Cancel();
                            _logger.LogWarning(
                                "signaling expired in flight after {Attempt} attempt(s); the backend may have applied it",
                                attempt);
                            PublishFailure("signaling_expired");
                            return;
                        }
                        await sendTask.ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException ex) when (_now() >= entry.ExpiresAt)
                    {
                        _logger.LogWarning(
                            "signaling expired in flight after {Attempt} attempt(s); the backend may have applied it: {Reason}",
                            attempt, ex.Message);
                        PublishFailure("signaling_expired");
                        return;
                    }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        if (attempt >= _options.SignalingMaxAttempts)
                        {
                            _logger.LogError(ex, "signaling delivery exhausted {Attempts} attempts", attempt);
                            PublishFailure("signaling_retry_exhausted");
                            return;
                        }
                        var remainingAfterFailure = entry.ExpiresAt - _now();
                        if (remainingAfterFailure <= TimeSpan.Zero)
                        {
                            _logger.LogWarning("signaling expired before retry");
                            PublishFailure("signaling_expired");
                            return;
                        }
                        var delay = RetryDelay(attempt);
                        var expiresDuringDelay = delay >= remainingAfterFailure;
                        if (expiresDuringDelay) delay = remainingAfterFailure;
                        _logger.LogWarning("signaling delivery failed ({Reason}); retry {Attempt} in {Delay}",
                            ex.Message, attempt, delay);
                        await Task.Delay(delay, token).ConfigureAwait(false);
                        if (expiresDuringDelay)
                        {
                            _logger.LogWarning("signaling expired while waiting to retry");
                            PublishFailure("signaling_expired");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "signaling delivery failed permanently: {Reason}", ex.Message);
                        PublishFailure("signaling_delivery_failed");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "signaling relay failed unexpectedly");
            PublishFailure("signaling_relay_failed");
        }
    }

    private TimeSpan RetryDelay(int attempt)
    {
        var seconds = _options.SignalingRetryBaseDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt - 1, 3));
        return TimeSpan.FromSeconds(Math.Min(seconds, 8));
    }

    /// <summary>
    /// Transient means the signal may still be delivered later. A backend result that is
    /// unknown (lost response) is included: the retry can duplicate an already committed
    /// signal, which the engine contract accepts.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        BackendException backend => backend.OutcomeUnknown || backend.Retryable ||
            backend.HttpStatus is 0 or 408 or 429 or >= 500,
        MilkyActionException milky => milky.Code is MilkyErrorCode.Transient,
        OperationCanceledException => true,
        IOException or System.Net.Sockets.SocketException or TimeoutException => true,
        _ => false,
    };

    private void PublishFailure(string reason)
    {
        if (Interlocked.Exchange(ref _failurePublished, 1) != 0) return;
        _stop.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            _onFailure(reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "signaling failure callback failed for {Reason}", reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _stop.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await Task.WhenAny(_worker, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
