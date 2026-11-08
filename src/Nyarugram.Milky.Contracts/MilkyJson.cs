using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nyarugram.Milky.Contracts;

/// <summary>
/// Serialization settings for the Milky HTTP JSON surface. Milky field names are
/// snake_case and every declared property of a response object must be present,
/// so nulls are written rather than omitted.
/// </summary>
public static class MilkyJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

/// <summary>Milky envelope: {"status","retcode","data"} or {"status","retcode","message"}.</summary>
public sealed class MilkyResponse
{
    [JsonPropertyName("status")] public string Status { get; init; } = "ok";
    [JsonPropertyName("retcode")] public int RetCode { get; init; }
    [JsonPropertyName("data")] public object? Data { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }

    public static MilkyResponse Ok(object data) => new() { Status = "ok", RetCode = 0, Data = data };
    public static MilkyResponse Failed(int retCode, string message) =>
        new() { Status = "failed", RetCode = retCode, Message = message };
}

/// <summary>Milky protocol error codes used by this adapter.</summary>
public static class MilkyRetCode
{
    public const int Ok = 0;
    public const int BadRequest = -400;
    public const int NotFound = -404;
    public const int NotLoggedIn = -403;
    public const int Unsupported = -501;
    public const int Conflict = -409;
    public const int RateLimited = -429;
    public const int Internal = -500;
}
