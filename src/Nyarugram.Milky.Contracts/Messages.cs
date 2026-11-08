using System.Text.Json.Serialization;

namespace Nyarugram.Milky.Contracts;

public abstract class IncomingMessage
{
    [JsonPropertyName("message_scene")] public abstract string MessageScene { get; }
    [JsonPropertyName("peer_id")] public long PeerId { get; init; }
    /// <summary>Stable Nyarugram message id, reused verbatim as the Milky sequence.</summary>
    [JsonPropertyName("message_seq")] public long MessageSeq { get; init; }
    [JsonPropertyName("sender_id")] public long SenderId { get; init; }
    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("segments")] public IReadOnlyList<MessageSegment> Segments { get; init; } = [];
}

public sealed class FriendMessage : IncomingMessage
{
    public override string MessageScene => "friend";
    [JsonPropertyName("friend")] public FriendEntity Friend { get; init; } = new();
}

public sealed class GroupMessage : IncomingMessage
{
    public override string MessageScene => "group";
    [JsonPropertyName("group")] public GroupEntity Group { get; init; } = new();
    [JsonPropertyName("group_member")] public GroupMemberEntity GroupMember { get; init; } = new();
}

/// <summary>
/// Declared only so capability reporting can name it. The backend has no temporary
/// conversation concept, so no temp message is ever produced.
/// </summary>
public sealed class TempMessage : IncomingMessage
{
    public override string MessageScene => "temp";
    [JsonPropertyName("group")] public GroupEntity? Group { get; init; }
}

public sealed class OutgoingMessage
{
    [JsonPropertyName("message_seq")] public long MessageSeq { get; init; }
    [JsonPropertyName("time")] public long Time { get; init; }
}

public sealed class LoginInfo
{
    [JsonPropertyName("uin")] public long Uin { get; init; }
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
}

public sealed class ImplInfo
{
    [JsonPropertyName("impl_name")] public string ImplName { get; init; } = "Nyarugram.Milky";
    [JsonPropertyName("impl_version")] public string ImplVersion { get; init; } = "";
    [JsonPropertyName("qq_protocol_version")] public string QqProtocolVersion { get; init; } = "";
    /// <summary>
    /// Schema-legal enum value describing the underlying client platform family
    /// (Nyarugram's client is an Android build). It does not imply QQ connectivity.
    /// </summary>
    [JsonPropertyName("qq_protocol_type")] public string QqProtocolType { get; init; } = "android_phone";
    [JsonPropertyName("milky_version")] public string MilkyVersion { get; init; } = "1.3";
}
