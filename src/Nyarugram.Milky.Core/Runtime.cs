using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Immutable snapshot of one authenticated binding. Every HTTP request, background
/// task, event stream and media callback captures it; results produced for an older
/// generation are discarded after logout or re-login.
/// </summary>
public sealed record SessionContext(
    AccountRecord Account,
    long Generation,
    string BearerToken)
{
    /// <summary>
    /// Cache scope: server identity, account UUID and login generation. Two accounts that
    /// happen to share a numeric identity never share cached profiles.
    /// </summary>
    public string Scope => $"{Account.ServerDomain}|{Account.AccountUuid}|{Generation}";
}

public sealed class MilkyRuntime
{
    private readonly ILogger<MilkyRuntime> _logger;
    private volatile SessionContext? _current;

    public MilkyRuntime(ILogger<MilkyRuntime> logger)
    {
        _logger = logger;
        InstanceId = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Identifies this OS process. It is independent of the login generation: restarting
    /// the process keeps the generation but must still invalidate live media handles.
    /// </summary>
    public string InstanceId { get; }

    public SessionContext? Current => _current;
    public bool IsLoggedIn => _current is not null;

    public SessionContext Require()
        => _current ?? throw new MilkyActionException(MilkyErrorCode.NotLoggedIn, "not logged in");

    /// <summary>Throws when a captured context is no longer the live binding.</summary>
    public void RequireCurrent(SessionContext context)
    {
        if (!IsCurrent(context))
            throw new MilkyActionException(MilkyErrorCode.NotLoggedIn, "session generation changed");
    }

    public void Bind(SessionContext context)
    {
        _current = context;
        _logger.LogInformation("session bound generation={Generation} account={Account} instance={Instance}",
            context.Generation, context.Account.AccountId, InstanceId);
    }

    public void Clear()
    {
        _current = null;
        _logger.LogInformation("session cleared instance={Instance}", InstanceId);
    }

    /// <summary>True when the captured context is still the live one.</summary>
    public bool IsCurrent(SessionContext context)
        => ReferenceEquals(_current, context) ||
           (_current is { } live && live.Account.AccountId == context.Account.AccountId && live.Generation == context.Generation);
}

public enum MilkyErrorCode
{
    BadRequest,
    NotFound,
    NotLoggedIn,
    Unsupported,
    Conflict,
    RateLimited,
    Internal,
    SendOutcomeUnknown,
    MediaUnavailable,

    /// <summary>A retryable transport or storage failure: the operation may be retried.</summary>
    Transient,
}

public sealed class MilkyActionException : Exception
{
    public MilkyActionException(MilkyErrorCode code, string message) : base(message) => Code = code;
    public MilkyErrorCode Code { get; }
}

/// <summary>
/// In-process cache scoped by server, account and login generation. Entries are keyed with
/// the scope prefix and dropped when the scope is invalidated, so a late response from a
/// previous session can never be served to the next one.
/// </summary>
public sealed class ProfileCache
{
    private readonly ConcurrentDictionary<string, (object Value, DateTimeOffset Expires)> _entries = new();
    private readonly TimeSpan _ttl;

    public ProfileCache(TimeSpan ttl) => _ttl = ttl;

    public bool TryGet<T>(string scope, string key, out T value)
    {
        if (_entries.TryGetValue(Compose(scope, key), out var entry)
            && entry.Expires > DateTimeOffset.UtcNow && entry.Value is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set<T>(string scope, string key, T value) where T : class
        => _entries[Compose(scope, key)] = (value, DateTimeOffset.UtcNow.Add(_ttl));

    public void Remove(string scope, string key) => _entries.TryRemove(Compose(scope, key), out _);

    /// <summary>Drops every entry belonging to a scope, e.g. on logout or re-login.</summary>
    public void InvalidateScope(string scope)
    {
        var prefix = scope + "|";
        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) _entries.TryRemove(key, out _);
        }
    }

    public int Count => _entries.Count;

    private static string Compose(string scope, string key) => scope + "|" + key;
}

public sealed class RuntimeOptions
{
    /// <summary>How long an undelivered Milky event may sit before retention expires it.</summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ProfileCacheTtl { get; init; } = TimeSpan.FromMinutes(10);
    public int SyncBatchLimit { get; init; } = 100;

    /// <summary>Requests per sync tick before yielding, to keep latency bounded.</summary>
    public int MaxSyncPagesPerTick { get; init; } = 20;

    public TimeSpan MediaTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Lease that serialises concurrent work on one idempotent operation.</summary>
    public TimeSpan OperationLease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a duplicate request waits for the in-flight operation to finish.</summary>
    public TimeSpan OperationWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Replay window announced to consumers; older rows are expired.</summary>
    public int ReplayBatchSize { get; init; } = 200;

    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Background difference polling. Disabled only by automated tests that drive the sync
    /// loop deterministically; production always runs it.
    /// </summary>
    public bool BackgroundSync { get; init; } = true;

    /// <summary>Automatic send reconciliation. Disabled by tests that drive it explicitly.</summary>
    public bool BackgroundSendReconcile { get; init; } = true;

    /// <summary>Periodic call maintenance (hangup retry and media recovery).</summary>
    public bool BackgroundCallMaintenance { get; init; } = true;

    /// <summary>Upper bound on media start attempts before the call is failed.</summary>
    public int MediaMaxAttempts { get; init; } = 6;

    /// <summary>Bounded outbound signaling queue per media instance.</summary>
    public int SignalingQueueCapacity { get; init; } = 64;

    /// <summary>Delivery attempts per engine signal before the call is failed.</summary>
    public int SignalingMaxAttempts { get; init; } = 5;

    public TimeSpan SignalingRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A queued signal older than this is no longer useful for the call.</summary>
    public TimeSpan SignalingMaxAge { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Default policy for incoming calls: never auto-answer unless enabled.</summary>
    public bool AutoAnswer { get; init; }
}
