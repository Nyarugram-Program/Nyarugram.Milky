using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;
using Xunit;

namespace Nyarugram.Milky.Tests;

/// <summary>Finding 8: profile caches must never leak across servers, accounts or sessions.</summary>
public sealed class CacheScopeTests
{
    private static AccountRecord Account(string domain, string uuid, long uin) => new(
        Guid.NewGuid(), domain, uuid, uin, "user", "User");

    private static SessionContext Context(AccountRecord account, long generation)
        => new(account, generation, "token");

    [Fact]
    public void Scope_includes_server_account_and_generation()
    {
        var account = Account("a.local", "uuid-1", 100_001);
        var first = Context(account, 1);
        var sameAccountNewSession = Context(account, 2);
        var otherAccountSameUin = Context(Account("a.local", "uuid-2", 100_001), 1);
        var otherServerSameAccount = Context(Account("b.local", "uuid-1", 100_001), 1);

        Assert.NotEqual(first.Scope, sameAccountNewSession.Scope);
        Assert.NotEqual(first.Scope, otherAccountSameUin.Scope);
        Assert.NotEqual(first.Scope, otherServerSameAccount.Scope);
        Assert.Equal(first.Scope, Context(account, 1).Scope);
    }

    [Fact]
    public void Cache_does_not_serve_entries_from_another_scope()
    {
        var cache = new ProfileCache(TimeSpan.FromMinutes(5));
        cache.Set("a.local|uuid-1|1", "user:100002", "Alice");

        Assert.True(cache.TryGet<string>("a.local|uuid-1|1", "user:100002", out var hit));
        Assert.Equal("Alice", hit);

        Assert.False(cache.TryGet<string>("a.local|uuid-1|2", "user:100002", out _));
        Assert.False(cache.TryGet<string>("a.local|uuid-2|1", "user:100002", out _));
        Assert.False(cache.TryGet<string>("b.local|uuid-1|1", "user:100002", out _));
    }

    [Fact]
    public void Invalidating_a_scope_drops_only_that_scope()
    {
        var cache = new ProfileCache(TimeSpan.FromMinutes(5));
        cache.Set("scope-a", "user:1", "A");
        cache.Set("scope-b", "user:1", "B");

        cache.InvalidateScope("scope-a");

        Assert.False(cache.TryGet<string>("scope-a", "user:1", out _));
        Assert.True(cache.TryGet<string>("scope-b", "user:1", out var remaining));
        Assert.Equal("B", remaining);
    }

    [Fact]
    public void Expired_entries_are_not_served()
    {
        var cache = new ProfileCache(TimeSpan.FromMilliseconds(30));
        cache.Set("scope", "user:1", "A");
        Thread.Sleep(80);
        Assert.False(cache.TryGet<string>("scope", "user:1", out _));
    }
}

/// <summary>Finding 3: only permanent failures may advance the sync cursor.</summary>
public sealed class SyncFailureClassificationTests
{
    [Fact]
    public void Unsupported_and_missing_content_is_permanent()
    {
        Assert.True(SyncService.IsPermanent(new MilkyActionException(MilkyErrorCode.Unsupported, "e2ee")));
        Assert.True(SyncService.IsPermanent(new MilkyActionException(MilkyErrorCode.NotFound, "gone")));
        Assert.False(SyncService.IsPermanent(new MilkyActionException(MilkyErrorCode.Transient, "timeout")));
        Assert.False(SyncService.IsPermanent(new MilkyActionException(MilkyErrorCode.Internal, "boom")));
        Assert.False(SyncService.IsPermanent(new MilkyActionException(MilkyErrorCode.NotLoggedIn, "stale")));
    }

    [Fact]
    public void Backend_error_classification_matches_retry_semantics()
    {
        Assert.True(SyncService.IsPermanent(new Backend.BackendException("USER_ID_INVALID", 404, false)));
        Assert.True(SyncService.IsPermanent(new Backend.BackendException("ENTITIES_TOO_LONG", 400, false)));
        Assert.True(SyncService.IsPermanent(new Backend.BackendException("BOTGRAM_RPC_UNSUPPORTED", 501, false)));
        Assert.False(SyncService.IsPermanent(new Backend.BackendException("BOTGRAM_STORAGE_FAILURE", 500, true)));
        Assert.False(SyncService.IsPermanent(new Backend.BackendException("BOTGRAM_TRANSPORT_TIMEOUT", 0, true)));
    }
}
