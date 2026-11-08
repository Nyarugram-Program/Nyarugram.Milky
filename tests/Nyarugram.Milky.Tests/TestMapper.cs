using Microsoft.Extensions.Logging.Abstractions;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Builds a mapper for segment-formatting tests. Identity-dependent paths (mention,
/// reply enrichment) are out of scope here and would require a live backend.
/// </summary>
internal static class TestMapper
{
    public static readonly AccountRecord Account = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "example.local", "user-uuid", 100_001, "bot", "Bot");

    public static MessageMapper Create() => new(
        rpc: null!,
        identities: null!,
        members: null!,
        NullLogger<MessageMapper>.Instance);
}
