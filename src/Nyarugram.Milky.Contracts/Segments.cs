using System.Text.Json.Serialization;

namespace Nyarugram.Milky.Contracts;

/// <summary>
/// One Milky message segment. <see cref="Type"/> is one of the Milky 1.3 segment
/// names; <see cref="Data"/> keeps the exact JSON declared by the pinned schema so
/// that unknown-but-valid segments survive a round trip unmodified.
/// </summary>
public sealed class MessageSegment
{
    [JsonPropertyName("type")] public string Type { get; init; } = "text";
    [JsonPropertyName("data")] public System.Text.Json.JsonElement Data { get; init; }

    public static MessageSegment Text(string text) => new()
    {
        Type = "text",
        Data = MilkySegments.Element(new TextData(text)),
    };

    public static MessageSegment Mention(long userId) => new()
    {
        Type = "mention",
        Data = MilkySegments.Element(new MentionData(userId)),
    };

    public static MessageSegment MentionAll() => new()
    {
        Type = "mention_all",
        Data = MilkySegments.Element(new EmptyData()),
    };

    public static MessageSegment Reply(long messageSeq) => new()
    {
        Type = "reply",
        Data = MilkySegments.Element(new ReplyData(messageSeq)),
    };
}

public static class MilkySegments
{
    private static readonly System.Text.Json.JsonSerializerOptions ElementOptions = new(MilkyJson.Options);

    public static System.Text.Json.JsonElement Element(object value) =>
        System.Text.Json.JsonSerializer.SerializeToElement(value, value.GetType(), ElementOptions);

    public static readonly System.Text.Json.JsonElement EmptyObject = Element(new EmptyData());
}

public sealed class EmptyData
{
}

public sealed class TextData
{
    public TextData()
    {
    }

    public TextData(string text) => Text = text;
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

public sealed class MentionData
{
    public MentionData()
    {
    }

    public MentionData(long userId) => UserId = userId;
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class ReplyData
{
    public ReplyData()
    {
    }

    public ReplyData(long messageSeq) => MessageSeq = messageSeq;
    [JsonPropertyName("message_seq")] public long MessageSeq { get; set; }
}
