using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Keeps the logged-in account shown as online while the adapter is serving.
///
/// The backend only refreshes presence for an RPC that carries an explicit
/// <c>foreground</c> flag within the last 60 seconds; background difference polling
/// deliberately does not extend the online window. The Android client sets the flag on
/// its own requests, so a session used only by this adapter would otherwise stay
/// permanently offline even though it is actively syncing.
///
/// This is a presence assertion only: it never affects message delivery, which is driven
/// by the durable difference cursor.
/// </summary>
public sealed class PresenceReporter : BackgroundService
{
    private readonly MilkyRuntime _runtime;
    private readonly BackendRpc _rpc;
    private readonly RuntimeOptions _options;
    private readonly ILogger<PresenceReporter> _logger;
    private DateTimeOffset _lastFailureLog = DateTimeOffset.MinValue;
    private bool _online;

    public PresenceReporter(
        MilkyRuntime runtime, BackendRpc rpc, RuntimeOptions options, ILogger<PresenceReporter> logger)
    {
        _runtime = runtime;
        _rpc = rpc;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.BackgroundPresence)
        {
            _logger.LogWarning(
                "foreground presence reporting is disabled by configuration; the account will appear offline");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var context = _runtime.Current;
            if (context is null)
            {
                _online = false;
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ReportOnceAsync(context, stoppingToken).ConfigureAwait(false);
                if (!_online)
                {
                    _logger.LogInformation(
                        "presence reported: the account stays online while serving (refresh every {Interval})",
                        _options.PresenceInterval);
                    _online = true;
                }
                else
                {
                    _logger.LogDebug("presence refreshed");
                }
            }
            catch (MilkyActionException ex) when (ex.Code == MilkyErrorCode.NotLoggedIn)
            {
                // The session was replaced or cleared; the next iteration binds to the new one.
                _online = false;
            }
            catch (BackendException ex)
            {
                _online = false;
                var now = DateTimeOffset.UtcNow;
                if (now - _lastFailureLog >= TimeSpan.FromMinutes(5))
                {
                    _lastFailureLog = now;
                    _logger.LogWarning(
                        "presence refresh failed {Code} retryable={Retryable}; the account may show offline",
                        ex.Code, ex.Retryable);
                }
                else
                {
                    _logger.LogDebug("presence refresh failed {Code}", ex.Code);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one failed heartbeat kill the loop; the next tick retries.
                _online = false;
                _logger.LogDebug(ex, "presence refresh failed unexpectedly");
            }

            try
            {
                await Task.Delay(_options.PresenceInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends one foreground heartbeat. Exposed so tests can drive the timing directly;
    /// <c>get_state</c> is the cheapest operation the backend serves.
    /// </summary>
    public async Task ReportOnceAsync(SessionContext context, CancellationToken ct)
    {
        await _rpc.CallAsync(context, new P.RpcRequest
        {
            Foreground = true,
            GetState = new P.GetStateRequest(),
        }, ct).ConfigureAwait(false);
    }
}
