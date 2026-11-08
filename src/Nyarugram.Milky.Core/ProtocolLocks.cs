using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nyarugram.Milky.Core;

public sealed class LockFile
{
    [JsonPropertyName("milky")] public LockMilky Milky { get; init; } = new();
    [JsonPropertyName("backend_protocol")] public LockBackend BackendProtocol { get; init; } = new();

    public sealed class LockMilky
    {
        [JsonPropertyName("version")] public string Version { get; init; } = "";
        [JsonPropertyName("schema_file")] public string SchemaFile { get; init; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
        [JsonPropertyName("source")] public string Source { get; init; } = "";
        [JsonPropertyName("source_commit")] public string SourceCommit { get; init; } = "";
    }

    public sealed class LockBackend
    {
        [JsonPropertyName("file")] public string File { get; init; } = "";
        [JsonPropertyName("package")] public string Package { get; init; } = "";
        [JsonPropertyName("protocol_major")] public int ProtocolMajor { get; init; } = 1;
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
        [JsonPropertyName("source")] public string Source { get; init; } = "";
        [JsonPropertyName("source_commit")] public string SourceCommit { get; init; } = "";
    }
}

/// <summary>
/// Loads the pinned protocol records and verifies the on-disk schema hash. A mismatch
/// is a startup failure: the adapter must never serve a schema it did not lock.
/// </summary>
public static class ProtocolLocks
{
    public static LockFile Load(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "config", "locks.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("config/locks.json is required; the Milky schema and backend proto must be pinned", path);

        var lockFile = JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("config/locks.json is malformed");

        var schemaPath = Path.Combine(contentRoot, lockFile.Milky.SchemaFile);
        if (File.Exists(schemaPath))
        {
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schemaPath))).ToLowerInvariant();
            if (!string.Equals(actual, lockFile.Milky.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Milky schema hash mismatch: expected {lockFile.Milky.Sha256}, found {actual}");
        }
        return lockFile;
    }

    public static string ImplementationVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
}
