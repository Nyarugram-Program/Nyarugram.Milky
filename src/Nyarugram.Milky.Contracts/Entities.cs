using System.Text.Json.Serialization;

namespace Nyarugram.Milky.Contracts;

/// <summary>Milky friend entity, mapped from a Nyarugram contact + user summary.</summary>
public sealed record FriendEntity
{
    [JsonPropertyName("user_id")] public long UserId { get; init; }
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
    /// <summary>Nyarugram stores no gender; "unknown" is the schema-legal value.</summary>
    [JsonPropertyName("sex")] public string Sex { get; init; } = "unknown";
    /// <summary>No QID equivalent exists; empty string per schema.</summary>
    [JsonPropertyName("qid")] public string Qid { get; init; } = "";
    [JsonPropertyName("remark")] public string Remark { get; init; } = "";
    [JsonPropertyName("category")] public FriendCategoryEntity Category { get; init; } = new();
}

public sealed record FriendCategoryEntity
{
    /// <summary>Nyarugram has no friend categories; a single synthetic bucket is used.</summary>
    [JsonPropertyName("category_id")] public long CategoryId { get; init; }
    [JsonPropertyName("category_name")] public string CategoryName { get; init; } = "";
}

public sealed record GroupEntity
{
    [JsonPropertyName("group_id")] public long GroupId { get; init; }
    [JsonPropertyName("group_name")] public string GroupName { get; init; } = "";
    [JsonPropertyName("member_count")] public long MemberCount { get; init; }
    /// <summary>Unknown capacity is reported as 0 rather than a fabricated limit.</summary>
    [JsonPropertyName("max_member_count")] public long MaxMemberCount { get; init; }
    [JsonPropertyName("remark")] public string Remark { get; init; } = "";
    [JsonPropertyName("created_time")] public long CreatedTime { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("question")] public string Question { get; init; } = "";
    [JsonPropertyName("announcement")] public string Announcement { get; init; } = "";
}

public sealed record GroupMemberEntity
{
    [JsonPropertyName("user_id")] public long UserId { get; init; }
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
    [JsonPropertyName("sex")] public string Sex { get; init; } = "unknown";
    [JsonPropertyName("group_id")] public long GroupId { get; init; }
    [JsonPropertyName("card")] public string Card { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("level")] public long Level { get; init; }
    /// <summary>
    /// Nyarugram's group API exposes participants without roles, so every member is
    /// reported as "member". Owner/admin promotion is not claimed.
    /// </summary>
    [JsonPropertyName("role")] public string Role { get; init; } = "member";
    [JsonPropertyName("join_time")] public long JoinTime { get; init; }
    [JsonPropertyName("last_sent_time")] public long LastSentTime { get; init; }
    [JsonPropertyName("shut_up_end_time")] public long? ShutUpEndTime { get; init; }
}

public sealed record UserProfileEntity
{
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
    [JsonPropertyName("qid")] public string Qid { get; init; } = "";
    [JsonPropertyName("age")] public long Age { get; init; }
    [JsonPropertyName("sex")] public string Sex { get; init; } = "unknown";
    [JsonPropertyName("remark")] public string Remark { get; init; } = "";
    [JsonPropertyName("bio")] public string Bio { get; init; } = "";
    [JsonPropertyName("level")] public long Level { get; init; }
    [JsonPropertyName("country")] public string Country { get; init; } = "";
    [JsonPropertyName("city")] public string City { get; init; } = "";
    [JsonPropertyName("school")] public string School { get; init; } = "";
}
