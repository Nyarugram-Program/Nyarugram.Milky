using System.Text.Json.Serialization;

namespace Nyarugram.Milky.Contracts;

// ---------------------------------------------------------------------------
// Nyarugram extensions. Every extension is namespaced with the nyarugram_ prefix
// and is declared through nyarugram_get_capabilities.
// ---------------------------------------------------------------------------

public sealed class NyarugramCapabilities
{
    [JsonPropertyName("extension_version")] public int ExtensionVersion { get; init; } = 1;
    [JsonPropertyName("impl_name")] public string ImplName { get; init; } = "Nyarugram.Milky";
    [JsonPropertyName("impl_version")] public string ImplVersion { get; init; } = "";
    [JsonPropertyName("milky_version")] public string MilkyVersion { get; init; } = "1.3";
    [JsonPropertyName("platform")] public string Platform { get; init; } = "nyarugram";
    [JsonPropertyName("backend_protocol_major")] public int BackendProtocolMajor { get; init; } = 1;
    [JsonPropertyName("backend_protocol_sha256")] public string BackendProtocolSha256 { get; init; } = "";
    [JsonPropertyName("milky_schema_sha256")] public string MilkySchemaSha256 { get; init; } = "";
    [JsonPropertyName("apis")] public IReadOnlyList<string> Apis { get; init; } = [];
    [JsonPropertyName("events")] public IReadOnlyList<string> Events { get; init; } = [];
    [JsonPropertyName("message_segments")] public IReadOnlyList<string> MessageSegments { get; init; } = [];
    [JsonPropertyName("message_scenes")] public IReadOnlyList<string> MessageScenes { get; init; } = [];
    [JsonPropertyName("calls")] public CapabilityCalls Calls { get; init; } = new();
    [JsonPropertyName("media")] public CapabilityMedia Media { get; init; } = new();
    [JsonPropertyName("limitations")] public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed class CapabilityCalls
{
    [JsonPropertyName("control")] public bool Control { get; init; }
    [JsonPropertyName("ring_only")] public bool RingOnly { get; init; }
    [JsonPropertyName("voice")] public bool Voice { get; init; }
    [JsonPropertyName("video")] public bool Video { get; init; }
    [JsonPropertyName("auto_answer")] public bool AutoAnswer { get; init; }
    /// <summary>Why voice/video are unavailable when the flag is false.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed class CapabilityMedia
{
    [JsonPropertyName("transport")] public string Transport { get; init; } = "websocket";
    [JsonPropertyName("path")] public string Path { get; init; } = "/nyarugram/calls/{call_id}/media";
    [JsonPropertyName("engine")] public string Engine { get; init; } = "none";
    [JsonPropertyName("opus_inbound")] public bool OpusInbound { get; init; }
    [JsonPropertyName("pcm_outbound")] public bool PcmOutbound { get; init; }
}

public sealed class ResolveUsernameInput
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
}

public sealed class ResolveUsernameOutput
{
    [JsonPropertyName("user_id")] public long UserId { get; init; }
    [JsonPropertyName("username")] public string Username { get; init; } = "";
}

public sealed class StartCallInput
{
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("video")] public bool Video { get; set; }
    /// <summary>Required client-supplied idempotency key.</summary>
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

public sealed class StartCallOutput
{
    [JsonPropertyName("call")] public CallInfo Call { get; set; } = new();
}

public sealed class AcceptCallInput
{
    [JsonPropertyName("call_id")] public string CallId { get; set; } = "";
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

public sealed class EndCallInput
{
    [JsonPropertyName("call_id")] public string CallId { get; set; } = "";
    /// <summary>hangup | missed | busy | disconnect | cancel | timeout | failed</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "hangup";
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

public sealed class GetCallInput
{
    [JsonPropertyName("call_id")] public string CallId { get; set; } = "";
}

public sealed class CallOutput
{
    [JsonPropertyName("call")] public CallInfo Call { get; set; } = new();
}

public sealed class OpenCallMediaInput
{
    [JsonPropertyName("call_id")] public string CallId { get; set; } = "";
    [JsonPropertyName("input")] public bool Input { get; set; } = true;
    [JsonPropertyName("output")] public bool Output { get; set; } = true;
}

public sealed class OpenCallMediaOutput
{
    [JsonPropertyName("call_id")] public string CallId { get; init; } = "";
    [JsonPropertyName("url_path")] public string UrlPath { get; init; } = "";
    [JsonPropertyName("token")] public string Token { get; init; } = "";
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; init; }
    [JsonPropertyName("media")] public CallMediaInfo Media { get; init; } = new();
}

public sealed class CallInfo
{
    [JsonPropertyName("call_id")] public string CallId { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "outgoing";
    [JsonPropertyName("peer_id")] public long PeerId { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("video")] public bool Video { get; init; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }
    [JsonPropertyName("connected_at")] public long? ConnectedAt { get; init; }
    [JsonPropertyName("ended_at")] public long? EndedAt { get; init; }
    [JsonPropertyName("end_reason")] public string? EndReason { get; init; }
    [JsonPropertyName("media_state")] public string MediaState { get; init; } = "none";
    [JsonPropertyName("media_reason")] public string? MediaReason { get; init; }
    [JsonPropertyName("state_version")] public long StateVersion { get; init; }
}
