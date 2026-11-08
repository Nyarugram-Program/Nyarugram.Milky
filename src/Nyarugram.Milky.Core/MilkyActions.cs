using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Storage;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Milky action surface. Every handler validates input against the pinned schema types,
/// maps onto the backend Protobuf API and returns the documented response envelope.
/// </summary>
public sealed class MilkyActions
{
    private readonly MilkyRuntime _runtime;
    private readonly BackendRpc _rpc;
    private readonly AccountApi _account;
    private readonly SendService _sends;
    private readonly IdentityResolver _identities;
    private readonly MessageMapper _mapper;
    private readonly CallService _calls;
    private readonly LockFile _locks;
    private readonly MediaCapability _capability;
    private readonly ILogger<MilkyActions> _logger;

    public MilkyActions(
        MilkyRuntime runtime, BackendRpc rpc, AccountApi account, SendService sends,
        IdentityResolver identities, MessageMapper mapper, CallService calls, LockFile locks,
        IMediaEngine engine, ILogger<MilkyActions> logger)
    {
        _runtime = runtime;
        _rpc = rpc;
        _account = account;
        _sends = sends;
        _identities = identities;
        _mapper = mapper;
        _calls = calls;
        _locks = locks;
        _capability = engine.Capability;
        _logger = logger;
    }

    public static readonly string[] SupportedActions =
    [
        "get_login_info", "get_impl_info", "get_user_profile",
        "get_friend_list", "get_friend_info",
        "get_group_list", "get_group_info",
        "get_group_member_list", "get_group_member_info",
        "send_private_message", "send_group_message",
        "get_message", "get_history_messages", "mark_message_as_read",
        "nyarugram_get_capabilities",
        "nyarugram_start_call", "nyarugram_accept_call", "nyarugram_end_call",
        "nyarugram_get_call", "nyarugram_open_call_media",
    ];

    public async Task<object> HandleAsync(string action, JsonElement body, CancellationToken ct)
    {
        // Actions that describe this adapter do not require a backend session.
        switch (action)
        {
            case "get_impl_info": return ImplInfo();
            case "nyarugram_get_capabilities": return Capabilities();
        }

        var context = _runtime.Require();
        return action switch
        {
            "get_login_info" => await LoginInfoAsync(context, ct).ConfigureAwait(false),
            "get_user_profile" => await UserProfileAsync(context, body, ct).ConfigureAwait(false),
            "get_friend_list" => await FriendListAsync(context, ct).ConfigureAwait(false),
            "get_friend_info" => await FriendInfoAsync(context, body, ct).ConfigureAwait(false),
            "get_group_list" => await GroupListAsync(context, ct).ConfigureAwait(false),
            "get_group_info" => await GroupInfoAsync(context, body, ct).ConfigureAwait(false),
            "get_group_member_list" => await GroupMemberListAsync(context, body, ct).ConfigureAwait(false),
            "get_group_member_info" => await GroupMemberInfoAsync(context, body, ct).ConfigureAwait(false),
            "send_private_message" => await SendAsync(context, body, PeerKind.User, ct).ConfigureAwait(false),
            "send_group_message" => await SendAsync(context, body, PeerKind.Group, ct).ConfigureAwait(false),
            "get_message" => await GetMessageAsync(context, body, ct).ConfigureAwait(false),
            "get_history_messages" => await HistoryAsync(context, body, ct).ConfigureAwait(false),
            "mark_message_as_read" => await MarkReadAsync(context, body, ct).ConfigureAwait(false),
            "nyarugram_start_call" => new StartCallOutput
            {
                Call = await _calls.StartAsync(context, Parse<StartCallInput>(body), ct).ConfigureAwait(false),
            },
            "nyarugram_accept_call" => new CallOutput
            {
                Call = await _calls.AcceptAsync(context, Parse<AcceptCallInput>(body).CallId, ct).ConfigureAwait(false),
            },
            "nyarugram_end_call" => new CallOutput
            {
                Call = await _calls.EndAsync(context, Parse<EndCallInput>(body), ct).ConfigureAwait(false),
            },
            "nyarugram_get_call" => new CallOutput
            {
                Call = await _calls.GetAsync(context, Parse<GetCallInput>(body).CallId, ct).ConfigureAwait(false),
            },
            "nyarugram_open_call_media" => await _calls.OpenMediaAsync(context, Parse<OpenCallMediaInput>(body), ct).ConfigureAwait(false),
            _ => throw new MilkyActionException(MilkyErrorCode.NotFound, $"unknown action '{action}'"),
        };
    }

    // ------------------------------------------------------------------- system

    private ImplInfo ImplInfo() => new()
    {
        ImplName = "Nyarugram.Milky",
        ImplVersion = ProtocolLocks.ImplementationVersion,
        QqProtocolVersion = $"nyarugram-rpc-v{_locks.BackendProtocol.ProtocolMajor}",
        QqProtocolType = "android_phone",
        MilkyVersion = _locks.Milky.Version.Split('.').Length >= 2
            ? string.Join('.', _locks.Milky.Version.Split('.').Take(2))
            : _locks.Milky.Version,
    };

    private NyarugramCapabilities Capabilities() => new()
    {
        ExtensionVersion = 1,
        ImplVersion = ProtocolLocks.ImplementationVersion,
        MilkyVersion = ImplInfo().MilkyVersion,
        BackendProtocolMajor = _locks.BackendProtocol.ProtocolMajor,
        BackendProtocolSha256 = _locks.BackendProtocol.Sha256,
        MilkySchemaSha256 = _locks.Milky.Sha256,
        Apis = SupportedActions,
        Events =
        [
            "message_receive",
            NyarugramEventType.CallIncoming,
            NyarugramEventType.CallStateChanged,
            NyarugramEventType.CallMediaReady,
            NyarugramEventType.CallMediaClosed,
        ],
        MessageSegments = ["text", "mention", "reply"],
        MessageScenes = ["friend", "group"],
        Calls = new CapabilityCalls
        {
            Control = _capability.Available,
            Voice = _capability.Audio,
            Video = _capability.Video,
            AutoAnswer = false,
            Reason = _capability.Available ? null : _capability.Reason,
        },
        Media = new CapabilityMedia
        {
            Engine = _capability.Engine,
            OpusInbound = false,
            PcmOutbound = false,
        },
        Limitations =
        [
            "uin and group_id are Nyarugram TL identities, not QQ numbers",
            "group member roles are always reported as member; the backend exposes no role",
            "max_member_count, qid, sex, level and remark have no backend source and use schema-legal empty values",
            "temp conversations are not declared because the backend has none",
            "media/file/record/video message segments are unsupported",
            "encrypted (E2EE) messages are never decrypted",
            _capability.Available
                ? "voice calls enabled; bidirectional PCM is verified between adapter instances, not against an Android device in this build"
                : "voice and video calls are unavailable in this build",
        ],
    };

    private async Task<GetLoginInfoOutput> LoginInfoAsync(SessionContext context, CancellationToken ct)
    {
        // The account plane lives on /api/v1/account; the business endpoint rejects it.
        var profile = await _account.MeAsync(context, ct).ConfigureAwait(false);
        return new GetLoginInfoOutput
        {
            Uin = profile?.TlUserId is > 0 ? profile.TlUserId : context.Account.Uin,
            Nickname = profile?.DisplayName ?? context.Account.DisplayName,
        };
    }

    // --------------------------------------------------------------------- users

    private async Task<GetUserProfileOutput> UserProfileAsync(
        SessionContext context, JsonElement body, CancellationToken ct)
    {
        var input = Parse<GetUserProfileInput>(body);
        var user = await _identities.RequireUserAsync(context, input.UserId, ct).ConfigureAwait(false);
        var full = await _rpc.CallAsync(context, new P.RpcRequest
        {
            GetFullUser = new P.GetFullUserRequest { User = new P.UserSelector { User = user.Identity } },
        }, ct).ConfigureAwait(false);

        return new GetUserProfileOutput
        {
            Profile = new UserProfileEntity
            {
                Nickname = MessageMapper.DisplayName(user),
                Bio = full.GetFullUser?.About ?? "",
                Remark = "",
                Qid = "",
                Age = 0,
                Sex = "unknown",
                Level = 0,
                Country = "",
                City = "",
                School = "",
            },
        };
    }

    private async Task<GetFriendListOutput> FriendListAsync(SessionContext context, CancellationToken ct)
    {
        var response = await _rpc.CallAsync(context, new P.RpcRequest { GetContacts = new P.GetContactsRequest() }, ct)
            .ConfigureAwait(false);
        var contacts = response.GetContacts;
        if (contacts is null) return new GetFriendListOutput();

        var users = contacts.Users.ToDictionary(u => u.Identity.UserId, u => u, StringComparer.Ordinal);
        var friends = new List<FriendEntity>(contacts.Contacts.Count);
        foreach (var contact in contacts.Contacts)
        {
            if (!users.TryGetValue(contact.User.UserId, out var user) || user.TlUserId <= 0) continue;
            await _identities.RememberSummariesAsync(context, [user], [], ct).ConfigureAwait(false);
            friends.Add(MessageMapper.MapFriend(user, user.TlUserId));
        }
        return new GetFriendListOutput { Friends = friends };
    }

    private async Task<GetFriendInfoOutput> FriendInfoAsync(
        SessionContext context, JsonElement body, CancellationToken ct)
    {
        var input = Parse<GetFriendInfoInput>(body);
        var user = await _identities.RequireUserAsync(context, input.UserId, ct).ConfigureAwait(false);
        return new GetFriendInfoOutput { Friend = MessageMapper.MapFriend(user, input.UserId) };
    }

    // -------------------------------------------------------------------- groups

    private async Task<GetGroupListOutput> GroupListAsync(SessionContext context, CancellationToken ct)
    {
        // The backend rejects a dialogs limit outside 1..100, so enumeration pages
        // through dialogs using the last entry as the offset until the page says the
        // slice is complete. Duplicate groups are collapsed by backend group id.
        var groups = new List<GroupEntity>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var seenOffsets = new HashSet<string>(StringComparer.Ordinal);
        P.Dialog? offset = null;
        while (true)
        {
            var request = new P.GetDialogsRequest { Limit = 100 };
            if (offset is not null)
            {
                request.OffsetMessageId = offset.TopMessageId;
                request.OffsetPeer = offset.Peer;
            }
            var response = await _rpc.CallAsync(context, new P.RpcRequest { GetDialogs = request }, ct)
                .ConfigureAwait(false);
            var page = response.GetDialogs;
            if (page is null) break;
            _logger.LogDebug("group list page offset={Offset} dialogs={Dialogs} groups={Groups} slice={Slice}",
                request.OffsetMessageId, page.Dialogs.Count, page.Groups.Count, page.IsSlice);

            await _identities.RememberSummariesAsync(context, page.Users, page.Groups, ct).ConfigureAwait(false);
            foreach (var group in page.Groups)
            {
                if (group.TlChatId <= 0 || !seenGroups.Add(group.GroupId)) continue;
                groups.Add(MessageMapper.MapGroup(group));
            }
            if (!page.IsSlice || page.Dialogs.Count == 0) break;
            var last = page.Dialogs[^1];
            if (!seenOffsets.Add($"{last.TopMessageId}|{last.Peer}")) break;
            offset = last;
        }
        return new GetGroupListOutput { Groups = groups };
    }

    private async Task<GetGroupInfoOutput> GroupInfoAsync(
        SessionContext context, JsonElement body, CancellationToken ct)
    {
        var input = Parse<GetGroupInfoInput>(body);
        var group = await _identities.RequireGroupAsync(context, input.GroupId, ct).ConfigureAwait(false);
        return new GetGroupInfoOutput { Group = MessageMapper.MapGroup(group) };
    }

    private async Task<GetGroupMemberListOutput> GroupMemberListAsync(
        SessionContext context, JsonElement body, CancellationToken ct)
    {
        var input = Parse<GetGroupMemberListInput>(body);
        var (group, members) = await LoadMembersAsync(context, input.GroupId, ct).ConfigureAwait(false);
        return new GetGroupMemberListOutput
        {
            Members = members.Select(m => m.Value with { GroupId = group.TlChatId }).ToList(),
        };
    }

    private async Task<GetGroupMemberInfoOutput> GroupMemberInfoAsync(
        SessionContext context, JsonElement body, CancellationToken ct)
    {
        var input = Parse<GetGroupMemberInfoInput>(body);
        var (group, members) = await LoadMembersAsync(context, input.GroupId, ct).ConfigureAwait(false);
        if (!members.TryGetValue(input.UserId, out var member))
            throw new MilkyActionException(MilkyErrorCode.NotFound, $"user {input.UserId} is not a member of {input.GroupId}");
        return new GetGroupMemberInfoOutput { Member = member with { GroupId = group.TlChatId } };
    }

    private async Task<(P.GroupSummary Group, Dictionary<long, GroupMemberEntity> Members)> LoadMembersAsync(
        SessionContext context, long groupUin, CancellationToken ct)
    {
        var group = await _identities.RequireGroupAsync(context, groupUin, ct).ConfigureAwait(false);
        var response = await _rpc.CallAsync(context, new P.RpcRequest
        {
            GetFullGroup = new P.GetFullGroupRequest { GroupId = group.GroupId },
        }, ct).ConfigureAwait(false);
        var full = response.GetFullGroup;
        var members = new Dictionary<long, GroupMemberEntity>();
        if (full is null) return (group, members);

        var names = full.Users.ToDictionary(u => u.Identity.UserId, u => u, StringComparer.Ordinal);
        foreach (var participant in full.Participants)
        {
            long uin;
            try
            {
                uin = await _identities.RequireUinForUserAsync(context, participant.User.UserId, ct).ConfigureAwait(false);
            }
            catch (MilkyActionException)
            {
                continue;
            }
            names.TryGetValue(participant.User.UserId, out var summary);
            members[uin] = new GroupMemberEntity
            {
                UserId = uin,
                Nickname = summary is null ? "" : MessageMapper.DisplayName(summary),
                GroupId = group.TlChatId,
                JoinTime = participant.JoinedAtMs / 1000,
            };
        }
        return (group, members);
    }

    // ------------------------------------------------------------------ messages

    private async Task<OutgoingMessage> SendAsync(
        SessionContext context, JsonElement body, PeerKind kind, CancellationToken ct)
    {
        long peerUin;
        List<MessageSegment> segments;
        string? idempotencyKey;
        if (kind == PeerKind.Group)
        {
            var input = Parse<SendGroupMessageInput>(body);
            peerUin = input.GroupId;
            segments = input.Message;
            idempotencyKey = input.IdempotencyKey;
        }
        else
        {
            var input = Parse<SendPrivateMessageInput>(body);
            peerUin = input.UserId;
            segments = input.Message;
            idempotencyKey = input.IdempotencyKey;
        }

        var outcome = await _sends
            .SendAsync(context, kind, peerUin, segments, idempotencyKey, ct)
            .ConfigureAwait(false);
        return new OutgoingMessage { MessageSeq = outcome.MessageSeq, Time = outcome.Time };
    }

    private async Task<GetMessageOutput> GetMessageAsync(
        SessionContext context, JsonElement body, CancellationToken ct)
    {
        var input = Parse<GetMessageInput>(body);
        var peerKind = ParseScene(input.MessageScene);
        var response = await _rpc.CallAsync(context, new P.RpcRequest
        {
            GetMessages = new P.GetMessagesRequest { MessageIds = { input.MessageSeq } },
        }, ct).ConfigureAwait(false);
        var message = response.GetMessages?.Messages.FirstOrDefault(m => m.MessageId == input.MessageSeq)
            ?? throw new MilkyActionException(MilkyErrorCode.NotFound, $"message {input.MessageSeq} not found");

        var mapped = await _mapper.ToMilkyAsync(context, message, ct).ConfigureAwait(false);
        if (mapped.PeerId != input.PeerId || (peerKind == PeerKind.Group) != (mapped is GroupMessage))
            throw new MilkyActionException(MilkyErrorCode.NotFound,
                "message does not belong to the requested conversation");
        return new GetMessageOutput { Message = mapped };
    }

    private async Task<GetHistoryMessagesOutput> HistoryAsync(
        SessionContext context, JsonElement body, CancellationToken ct)
    {
        var input = Parse<GetHistoryMessagesInput>(body);
        var limit = Math.Clamp(input.Limit ?? 20, 1, 30);
        var kind = ParseScene(input.MessageScene);
        if (kind == PeerKind.Group && input.MessageScene == "temp")
            throw new MilkyActionException(MilkyErrorCode.Unsupported, "temp conversations are not supported");

        var peer = await _identities.RequirePeerAsync(context, kind, input.PeerId, ct).ConfigureAwait(false);
        var response = await _rpc.CallAsync(context, new P.RpcRequest
        {
            GetHistory = new P.GetHistoryRequest
            {
                Peer = peer,
                OffsetMessageId = input.StartMessageSeq ?? 0,
                Limit = limit,
            },
        }, ct).ConfigureAwait(false);
        var page = response.GetHistory ?? new P.MessagePage();

        var messages = new List<IncomingMessage>(page.Messages.Count);
        foreach (var message in page.Messages.OrderBy(m => m.MessageId))
        {
            try
            {
                messages.Add(await _mapper.ToMilkyAsync(context, message, ct).ConfigureAwait(false));
            }
            catch (MilkyActionException ex)
            {
                _logger.LogDebug("history message {Id} skipped: {Reason}", message.MessageId, ex.Message);
            }
        }

        long? next = messages.Count == limit ? messages.Min(m => m.MessageSeq) : null;
        return new GetHistoryMessagesOutput { Messages = messages, NextMessageSeq = next };
    }

    private async Task<object> MarkReadAsync(SessionContext context, JsonElement body, CancellationToken ct)
    {
        var input = Parse<MarkMessageAsReadInput>(body);
        var kind = ParseScene(input.MessageScene);
        var peer = await _identities.RequirePeerAsync(context, kind, input.PeerId, ct).ConfigureAwait(false);
        await _rpc.CallAsync(context, new P.RpcRequest
        {
            ReadHistory = new P.ReadHistoryRequest { Peer = peer, MaxMessageId = input.MessageSeq },
        }, ct).ConfigureAwait(false);
        return new { };
    }

    // ------------------------------------------------------------------- helpers

    private static PeerKind ParseScene(string scene) => scene switch
    {
        "friend" => PeerKind.User,
        "group" => PeerKind.Group,
        "temp" => throw new MilkyActionException(MilkyErrorCode.Unsupported, "temp conversations are not supported"),
        _ => throw new MilkyActionException(MilkyErrorCode.BadRequest, $"unknown message_scene '{scene}'"),
    };

    private static T Parse<T>(JsonElement body) where T : new()
    {
        if (body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(body.GetRawText(), MilkyJson.Options) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new MilkyActionException(MilkyErrorCode.BadRequest, "invalid request body: " + ex.Message);
        }
    }

    private static MilkyActionException MapBackend(BackendException ex) => ex.HttpStatus switch
    {
        401 or 403 => new MilkyActionException(MilkyErrorCode.NotLoggedIn, ex.Code),
        404 => new MilkyActionException(MilkyErrorCode.NotFound, ex.Code),
        409 => new MilkyActionException(MilkyErrorCode.Conflict, ex.Code),
        429 => new MilkyActionException(MilkyErrorCode.RateLimited, ex.Code),
        501 => new MilkyActionException(MilkyErrorCode.Unsupported, ex.Code),
        400 => new MilkyActionException(MilkyErrorCode.BadRequest, ex.Code),
        _ => new MilkyActionException(MilkyErrorCode.Internal, ex.Code),
    };

    private static long RandomId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        var value = BitConverter.ToInt64(buffer);
        return value == 0 ? 1 : value;
    }
}
