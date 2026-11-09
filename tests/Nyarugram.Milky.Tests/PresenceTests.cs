using Nyarugram.Milky.Core;
using Xunit;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// The backend only marks an account online for a session that reported foreground
/// activity within the last 60 seconds. The adapter must assert that explicitly: its
/// difference polling deliberately omits the flag.
/// </summary>
[Collection("adapter")]
public sealed class PresenceTests
{
    private readonly AdapterFixture _fixture;

    public PresenceTests(AdapterFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Presence_heartbeat_reports_foreground_get_state()
    {
        if (_fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {_fixture.StartupError.Message}");

        await _fixture.Get<PresenceReporter>().ReportOnceAsync(
            _fixture.Runtime.Require(), CancellationToken.None);

        var request = _fixture.Backend.LastBusinessRequest;
        Assert.NotNull(request);
        Assert.Equal(P.RpcRequest.OperationOneofCase.GetState, request!.Value.Operation);
        Assert.True(request.Value.Foreground);
    }

    [SkippableFact]
    public async Task Difference_polling_does_not_assert_foreground()
    {
        if (_fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {_fixture.StartupError.Message}");

        await _fixture.SyncAsync();

        var request = _fixture.Backend.LastBusinessRequest;
        Assert.NotNull(request);
        Assert.Equal(P.RpcRequest.OperationOneofCase.GetDifference, request!.Value.Operation);
        // Omission keeps the heartbeat authoritative: polling never extends the online window.
        Assert.Null(request.Value.Foreground);
    }
}
