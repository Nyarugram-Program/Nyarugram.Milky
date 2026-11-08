using System.Text.Json.Serialization;

namespace Nyarugram.Milky.Contracts;

public sealed class GetUserProfileInput
{
    [JsonPropertyName("user_id")] public long UserId { get; set; }
}

public sealed class GetUserProfileOutput
{
    [JsonPropertyName("profile")] public UserProfileEntity Profile { get; set; } = new();
}

public sealed class GetFriendInfoInput
{
    [JsonPropertyName("user_id")] public long UserId { get; set; }
}

public sealed class GetFriendInfoOutput
{
    [JsonPropertyName("friend")] public FriendEntity Friend { get; set; } = new();
}

public sealed class GetGroupInfoInput
{
    [JsonPropertyName("group_id")] public long GroupId { get; set; }
    [JsonPropertyName("no_cache")] public bool? NoCache { get; set; }
}

public sealed class GetGroupInfoOutput
{
    [JsonPropertyName("group")] public GroupEntity Group { get; set; } = new();
}

public sealed class GetGroupMemberListInput
{
    [JsonPropertyName("group_id")] public long GroupId { get; set; }
    [JsonPropertyName("no_cache")] public bool? NoCache { get; set; }
}

public sealed class GetGroupMemberListOutput
{
    [JsonPropertyName("members")] public IReadOnlyList<GroupMemberEntity> Members { get; set; } = [];
}

public sealed class GetGroupMemberInfoInput
{
    [JsonPropertyName("group_id")] public long GroupId { get; set; }
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("no_cache")] public bool? NoCache { get; set; }
}

public sealed class GetGroupMemberInfoOutput
{
    [JsonPropertyName("member")] public GroupMemberEntity Member { get; set; } = new();
}

public sealed class SendPrivateMessageInput
{
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("message")] public List<MessageSegment> Message { get; set; } = [];

    /// <summary>
    /// Nyarugram extension: optional idempotency key. Repeating the same key with the same
    /// content replays the original result; a different payload conflicts.
    /// </summary>
    [JsonPropertyName("nyarugram_idempotency_key")] public string? IdempotencyKey { get; set; }
}

public sealed class SendGroupMessageInput
{
    [JsonPropertyName("group_id")] public long GroupId { get; set; }
    [JsonPropertyName("message")] public List<MessageSegment> Message { get; set; } = [];

    /// <summary>See <see cref="SendPrivateMessageInput.IdempotencyKey"/>.</summary>
    [JsonPropertyName("nyarugram_idempotency_key")] public string? IdempotencyKey { get; set; }
}

public sealed class GetMessageInput
{
    [JsonPropertyName("message_scene")] public string MessageScene { get; set; } = "friend";
    [JsonPropertyName("peer_id")] public long PeerId { get; set; }
    [JsonPropertyName("message_seq")] public long MessageSeq { get; set; }
}

public sealed class GetMessageOutput
{
    [JsonPropertyName("message")] public IncomingMessage Message { get; set; } = null!;
}

public sealed class GetHistoryMessagesInput
{
    [JsonPropertyName("message_scene")] public string MessageScene { get; set; } = "friend";
    [JsonPropertyName("peer_id")] public long PeerId { get; set; }
    [JsonPropertyName("start_message_seq")] public long? StartMessageSeq { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
}

public sealed class GetHistoryMessagesOutput
{
    [JsonPropertyName("messages")] public IReadOnlyList<IncomingMessage> Messages { get; set; } = [];
    [JsonPropertyName("next_message_seq")] public long? NextMessageSeq { get; set; }
}

public sealed class MarkMessageAsReadInput
{
    [JsonPropertyName("message_scene")] public string MessageScene { get; set; } = "friend";
    [JsonPropertyName("peer_id")] public long PeerId { get; set; }
    [JsonPropertyName("message_seq")] public long MessageSeq { get; set; }
}

public sealed class GetLoginInfoOutput
{
    [JsonPropertyName("uin")] public long Uin { get; set; }
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
}

public sealed class GetFriendListOutput
{
    [JsonPropertyName("friends")] public IReadOnlyList<FriendEntity> Friends { get; set; } = [];
}

public sealed class GetGroupListOutput
{
    [JsonPropertyName("groups")] public IReadOnlyList<GroupEntity> Groups { get; set; } = [];
}
