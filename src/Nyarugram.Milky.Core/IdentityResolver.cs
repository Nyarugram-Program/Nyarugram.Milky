using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Storage;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Issues one RPC under a captured session generation. Request ids are ASCII and
/// unique per call; a stale generation aborts before contacting the backend.
/// </summary>
public sealed class BackendRpc
{
    private readonly BackendClient _client;
    private readonly MilkyRuntime _runtime;

    public BackendRpc(BackendClient client, MilkyRuntime runtime)
    {
        _client = client;
        _runtime = runtime;
    }

    public static string NewRequestId() => "m" + Guid.NewGuid().ToString("N");

    public async Task<P.RpcResponse> CallAsync(SessionContext context, P.RpcRequest request, CancellationToken ct)
    {
        if (!_runtime.IsCurrent(context))
            throw new MilkyActionException(MilkyErrorCode.NotLoggedIn, "session generation changed");
        request.RequestId = NewRequestId();
        var response = await _client.RpcAsync(request, context.BearerToken, ct).ConfigureAwait(false);
        if (!_runtime.IsCurrent(context))
            throw new MilkyActionException(MilkyErrorCode.NotLoggedIn, "session generation changed");
        return response;
    }
}

/// <summary>
/// Maps Milky numeric identities to Nyarugram UUIDs and back. The numeric value is the
/// backend's stable TL id; this class never derives an id from a hash of a UUID.
/// </summary>
public sealed class IdentityResolver
{
    private readonly BackendRpc _rpc;
    private readonly IdentityStore _store;
    private readonly ProfileCache _cache;
    private readonly MilkyRuntime _runtime;
    private readonly ILogger<IdentityResolver> _logger;

    public IdentityResolver(
        BackendRpc rpc, IdentityStore store, ProfileCache cache, MilkyRuntime runtime,
        ILogger<IdentityResolver> logger)
    {
        _rpc = rpc;
        _store = store;
        _cache = cache;
        _runtime = runtime;
        _logger = logger;
    }

    public async Task<P.UserSummary> RequireUserAsync(SessionContext context, long uin, CancellationToken ct)
    {
        var summary = await FindUserAsync(context, uin, ct).ConfigureAwait(false);
        return summary ?? throw new MilkyActionException(MilkyErrorCode.NotFound, $"user {uin} is not resolvable");
    }

    public async Task<P.UserSummary?> FindUserAsync(SessionContext context, long uin, CancellationToken ct)
    {
        if (uin <= 0) return null;
        var key = $"user:{uin}";
        if (_cache.TryGet(context.Scope, key, out P.UserSummary? cached)) return cached;

        var summary = await LoadUserAsync(context, uin, ct).ConfigureAwait(false);
        if (summary is null) return null;
        _runtime.RequireCurrent(context);
        _cache.Set(context.Scope, key, summary);
        await _store.RememberAsync(context.Account.AccountId, PeerKind.User, summary.Identity.UserId, uin, ct)
            .ConfigureAwait(false);
        return summary;
    }

    private async Task<P.UserSummary?> LoadUserAsync(SessionContext context, long uin, CancellationToken ct)
    {
        var external = await _store.FindExternalIdAsync(context.Account.AccountId, PeerKind.User, uin, ct)
            .ConfigureAwait(false);
        if (external is not null)
        {
            var response = await _rpc.CallAsync(context, new P.RpcRequest
            {
                GetUsers = new P.GetUsersRequest
                {
                    Users =
                    {
                        new P.UserSelector
                        {
                            User = new P.UserRef
                            {
                                ServerDomain = context.Account.ServerDomain,
                                UserId = external,
                            },
                        },
                    },
                },
            }, ct).ConfigureAwait(false);
            if (response.GetUsers?.Users.Count > 0) return MatchTl(response.GetUsers.Users, uin);
        }

        var resolved = await _rpc.CallAsync(context, new P.RpcRequest
        {
            ResolveIdentities = new P.ResolveIdentitiesRequest { TlUserIds = { uin } },
        }, ct).ConfigureAwait(false);
        var found = MatchTl(resolved.ResolveIdentities?.Users, uin);
        if (found is null) _logger.LogDebug("unresolved user uin={Uin}", uin);
        return found;
    }

    public async Task<P.GroupSummary> RequireGroupAsync(SessionContext context, long groupUin, CancellationToken ct)
    {
        var summary = await FindGroupAsync(context, groupUin, ct).ConfigureAwait(false);
        return summary ?? throw new MilkyActionException(MilkyErrorCode.NotFound, $"group {groupUin} is not resolvable");
    }

    public async Task<P.GroupSummary?> FindGroupAsync(SessionContext context, long groupUin, CancellationToken ct)
    {
        if (groupUin <= 0) return null;
        var key = $"group:{groupUin}";
        if (_cache.TryGet(context.Scope, key, out P.GroupSummary? cached)) return cached;

        var external = await _store.FindExternalIdAsync(context.Account.AccountId, PeerKind.Group, groupUin, ct)
            .ConfigureAwait(false);
        P.GroupSummary? summary = null;
        if (external is not null)
        {
            var response = await _rpc.CallAsync(context, new P.RpcRequest
            {
                GetGroups = new P.GetGroupsRequest { GroupIds = { external } },
            }, ct).ConfigureAwait(false);
            summary = MatchChat(response.GetGroups?.Groups, groupUin);
        }

        if (summary is null)
        {
            var resolved = await _rpc.CallAsync(context, new P.RpcRequest
            {
                ResolveIdentities = new P.ResolveIdentitiesRequest { TlChatIds = { groupUin } },
            }, ct).ConfigureAwait(false);
            summary = MatchChat(resolved.ResolveIdentities?.Groups, groupUin);
        }

        if (summary is null) return null;
        _runtime.RequireCurrent(context);
        _cache.Set(context.Scope, key, summary);
        await _store.RememberAsync(context.Account.AccountId, PeerKind.Group, summary.GroupId, groupUin, ct)
            .ConfigureAwait(false);
        return summary;
    }

    /// <summary>Builds a PeerRef for a Milky peer number.</summary>
    public async Task<P.PeerRef> RequirePeerAsync(SessionContext context, PeerKind kind, long uin, CancellationToken ct)
    {
        if (kind == PeerKind.Group)
        {
            var group = await RequireGroupAsync(context, uin, ct).ConfigureAwait(false);
            return new P.PeerRef { GroupId = group.GroupId };
        }
        var user = await RequireUserAsync(context, uin, ct).ConfigureAwait(false);
        return new P.PeerRef { User = user.Identity };
    }

    /// <summary>Remembers authoritative TL ids carried in a difference or message payload.</summary>
    public async Task RememberSummariesAsync(
        SessionContext context, IEnumerable<P.UserSummary> users, IEnumerable<P.GroupSummary> groups, CancellationToken ct)
    {
        foreach (var user in users)
        {
            if (user.TlUserId <= 0) continue;
            await _store.RememberAsync(context.Account.AccountId, PeerKind.User, user.Identity.UserId, user.TlUserId, ct)
                .ConfigureAwait(false);
            _cache.Set(context.Scope, $"user:{user.TlUserId}", user);
        }
        foreach (var group in groups)
        {
            if (group.TlChatId <= 0) continue;
            await _store.RememberAsync(context.Account.AccountId, PeerKind.Group, group.GroupId, group.TlChatId, ct)
                .ConfigureAwait(false);
            _cache.Set(context.Scope, $"group:{group.TlChatId}", group);
        }
    }

    /// <summary>
    /// Resolves the numeric id for a known backend user UUID, fetching and validating the
    /// summary the first time. Never derives a number from the UUID itself.
    /// </summary>
    public async Task<long> RequireUinForUserAsync(SessionContext context, string userUuid, CancellationToken ct)
    {
        var known = await _store.FindUinAsync(context.Account.AccountId, PeerKind.User, userUuid, ct).ConfigureAwait(false);
        if (known is { } uin) return uin;

        var response = await _rpc.CallAsync(context, new P.RpcRequest
        {
            GetUsers = new P.GetUsersRequest
            {
                Users =
                {
                    new P.UserSelector
                    {
                        User = new P.UserRef
                        {
                            ServerDomain = context.Account.ServerDomain,
                            UserId = userUuid,
                        },
                    },
                },
            },
        }, ct).ConfigureAwait(false);
        var summary = response.GetUsers?.Users.FirstOrDefault(u => u.Identity.UserId == userUuid);
        if (summary is null || summary.TlUserId <= 0)
            throw new MilkyActionException(MilkyErrorCode.NotFound, $"user {userUuid} has no stable numeric identity");

        await _store.RememberAsync(context.Account.AccountId, PeerKind.User, userUuid, summary.TlUserId, ct)
            .ConfigureAwait(false);
        _runtime.RequireCurrent(context);
        _cache.Set(context.Scope, $"user:{summary.TlUserId}", summary);
        return summary.TlUserId;
    }

    /// <summary>
    /// Resolves an "@username" mention to its summarized identity. The result is cached
    /// per session scope; an unknown username returns null so the caller keeps the text.
    /// </summary>
    public async Task<P.UserSummary?> FindByUsernameAsync(
        SessionContext context, string username, CancellationToken ct)
    {
        var key = $"username:{username.ToLowerInvariant()}";
        if (_cache.TryGet(context.Scope, key, out P.UserSummary cached)) return cached;

        var response = await _rpc.CallAsync(context, new P.RpcRequest
        {
            ResolveUsername = new P.ResolveUsernameRequest { Username = username },
        }, ct).ConfigureAwait(false);
        var summary = response.ResolveUsername?.Users.FirstOrDefault(u =>
            u.TlUserId > 0 && string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        if (summary is null) return null;

        await RememberSummariesAsync(context, [summary], [], ct).ConfigureAwait(false);
        _runtime.RequireCurrent(context);
        _cache.Set(context.Scope, key, summary);
        return summary;
    }

    public async Task<long> RequireUinForGroupAsync(SessionContext context, string groupUuid, CancellationToken ct)
    {
        var known = await _store.FindUinAsync(context.Account.AccountId, PeerKind.Group, groupUuid, ct).ConfigureAwait(false);
        if (known is { } uin) return uin;

        var response = await _rpc.CallAsync(context, new P.RpcRequest
        {
            GetGroups = new P.GetGroupsRequest { GroupIds = { groupUuid } },
        }, ct).ConfigureAwait(false);
        var summary = response.GetGroups?.Groups.FirstOrDefault(g => g.GroupId == groupUuid);
        if (summary is null || summary.TlChatId <= 0)
            throw new MilkyActionException(MilkyErrorCode.NotFound, $"group {groupUuid} has no stable numeric identity");

        await _store.RememberAsync(context.Account.AccountId, PeerKind.Group, groupUuid, summary.TlChatId, ct)
            .ConfigureAwait(false);
        _runtime.RequireCurrent(context);
        _cache.Set(context.Scope, $"group:{summary.TlChatId}", summary);
        return summary.TlChatId;
    }

    private static P.UserSummary? MatchTl(IEnumerable<P.UserSummary>? users, long uin)
        => users?.FirstOrDefault(u => u.TlUserId == uin);
    private static P.GroupSummary? MatchChat(IEnumerable<P.GroupSummary>? groups, long groupUin)
        => groups?.FirstOrDefault(g => g.TlChatId == groupUin);
}
