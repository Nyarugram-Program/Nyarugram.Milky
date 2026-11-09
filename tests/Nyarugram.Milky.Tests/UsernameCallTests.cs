using Nyarugram.Milky.Contracts;
using Xunit;

namespace Nyarugram.Milky.Tests;

[Collection("adapter")]
public sealed class UsernameCallTests
{
    private readonly AdapterFixture _fixture;
    public UsernameCallTests(AdapterFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Username_resolves_through_protobuf_and_can_start_and_end_call()
    {
        if (_fixture.StartupError is not null) throw new SkipException("Isolated PostgreSQL unavailable");
        var resolved = await _fixture.CallOkAsync("nyarugram_resolve_username", new { username = "@ALICE" });
        var userId = resolved.GetProperty("user_id").GetInt64();
        Assert.Equal(FakeBackend.FriendUin, userId);
        Assert.Equal(FakeBackend.FriendUsername, resolved.GetProperty("username").GetString());
        var requestId = "qq-call-" + Guid.NewGuid().ToString("N");
        var input = new { user_id = userId, request_id = requestId, video = false };
        var started = await _fixture.CallOkAsync("nyarugram_start_call", input);
        var callId = started.GetProperty("call").GetProperty("call_id").GetString();
        try
        {
            var replay = await _fixture.CallOkAsync("nyarugram_start_call", input);
            Assert.Equal(callId, replay.GetProperty("call").GetProperty("call_id").GetString());
        }
        finally
        {
            await _fixture.CallOkAsync("nyarugram_end_call", new
            {
                call_id = callId, reason = "cancel", request_id = "end-" + requestId,
            });
        }
    }

    [SkippableTheory]
    [InlineData("unknown_username", MilkyRetCode.NotFound)]
    [InlineData("", MilkyRetCode.BadRequest)]
    [InlineData("alice bob", MilkyRetCode.BadRequest)]
    public async Task Username_errors_are_explicit(string username, int expected)
    {
        if (_fixture.StartupError is not null) throw new SkipException("Isolated PostgreSQL unavailable");
        var response = await _fixture.CallAsync("nyarugram_resolve_username", new { username });
        Assert.Equal(expected, response.GetProperty("retcode").GetInt32());
    }
}