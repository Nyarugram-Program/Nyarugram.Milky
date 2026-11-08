using System.Text.Json.Serialization;

namespace Nyarugram.Milky.Contracts;

/// <summary>Envelope shared by every Milky pushed event.</summary>
public sealed class MilkyEvent
{
    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("self_id")] public long SelfId { get; init; }
    [JsonPropertyName("event_type")] public string EventType { get; init; } = "";
    [JsonPropertyName("data")] public object Data { get; init; } = new();
}

public static class NyarugramEventType
{
    public const string CallIncoming = "nyarugram_call_incoming";
    public const string CallStateChanged = "nyarugram_call_state_changed";
    public const string CallMediaReady = "nyarugram_call_media_ready";
    public const string CallMediaClosed = "nyarugram_call_media_closed";
}

public sealed class CallEventData
{
    [JsonPropertyName("event_id")] public string EventId { get; init; } = "";
    [JsonPropertyName("call_id")] public string CallId { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "outgoing";
    [JsonPropertyName("peer_id")] public long PeerId { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("previous_state")] public string? PreviousState { get; init; }
    /// <summary>Monotonic per-call state version; receivers may drop stale events.</summary>
    [JsonPropertyName("state_version")] public long StateVersion { get; init; }
    [JsonPropertyName("video")] public bool Video { get; init; }
    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("media")] public CallMediaInfo? Media { get; init; }
}

public sealed class CallMediaInfo
{
    [JsonPropertyName("format")] public string Format { get; init; } = "pcm_s16le";
    [JsonPropertyName("sample_rate")] public int SampleRate { get; init; } = 48000;
    [JsonPropertyName("channels")] public int Channels { get; init; } = 1;
    [JsonPropertyName("frame_ms")] public int FrameMs { get; init; } = 20;
    [JsonPropertyName("frame_bytes")] public int FrameBytes { get; init; } = 1920;
}
