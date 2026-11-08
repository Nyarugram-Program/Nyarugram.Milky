using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Storage;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Lazily loads group participants so an incoming group message can carry a
/// group_member entity. Missing data degrades to a schema-legal minimal member rather
/// than inventing a role or join time.
/// </summary>
public sealed class GroupMemberDirectory
{
    private readonly BackendRpc _rpc;
    private readonly IdentityResolver _identities;
    private readonly ProfileCache _cache;
    private readonly ILogger<GroupMemberDirectory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GroupMemberDirectory(
        BackendRpc rpc, IdentityResolver identities, ProfileCache cache, ILogger<GroupMemberDirectory> logger)
    {
        _rpc = rpc;
        _identities = identities;
        _cache = cache;
        _logger = logger;
    }

    public async Task<GroupMemberEntity> GetAsync(
        SessionContext context, long groupUin, long userUin, CancellationToken ct)
    {
        var key = $"members:{groupUin}";
        if (!_cache.TryGet(context.Scope, key, out Dictionary<long, GroupMemberEntity>? members))
        {
            members = await LoadAsync(context, groupUin, ct).ConfigureAwait(false);
            if (members.Count > 0) _cache.Set(context.Scope, key, members);
        }

        if (members is not null && members.TryGetValue(userUin, out var found)) return found;

        var user = await _identities.FindUserAsync(context, userUin, ct).ConfigureAwait(false);
        return new GroupMemberEntity
        {
            UserId = userUin,
            Nickname = user is null ? "" : MessageMapper.DisplayName(user),
            GroupId = groupUin,
        };
    }

    private async Task<Dictionary<long, GroupMemberEntity>> LoadAsync(
        SessionContext context, long groupUin, CancellationToken ct)
    {
        var result = new Dictionary<long, GroupMemberEntity>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var group = await _identities.FindGroupAsync(context, groupUin, ct).ConfigureAwait(false);
            if (group is null) return result;
            var response = await _rpc.CallAsync(context, new P.RpcRequest
            {
                GetFullGroup = new P.GetFullGroupRequest { GroupId = group.GroupId },
            }, ct).ConfigureAwait(false);
            var full = response.GetFullGroup;
            if (full is null) return result;

            var names = full.Users.ToDictionary(u => u.Identity.UserId, u => u, StringComparer.Ordinal);
            foreach (var participant in full.Participants)
            {
                var uuid = participant.User.UserId;
                long uin;
                try
                {
                    uin = await _identities.RequireUinForUserAsync(context, uuid, ct).ConfigureAwait(false);
                }
                catch (MilkyActionException)
                {
                    continue;
                }
                names.TryGetValue(uuid, out var summary);
                result[uin] = new GroupMemberEntity
                {
                    UserId = uin,
                    Nickname = summary is null ? "" : MessageMapper.DisplayName(summary),
                    GroupId = groupUin,
                    JoinTime = participant.JoinedAtMs / 1000,
                    // The backend exposes participants without roles; "member" is the only
                    // truthful value.
                    Role = "member",
                };
            }
        }
        catch (Backend.BackendException ex)
        {
            _logger.LogDebug(ex, "group {Group} member directory unavailable", groupUin);
        }
        finally
        {
            _gate.Release();
        }
        return result;
    }
}
