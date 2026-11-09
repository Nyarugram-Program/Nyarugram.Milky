using Microsoft.Extensions.DependencyInjection;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;
using Xunit;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

[Collection("adapter")]
public sealed class RingOnlyCallTests
{
    private readonly AdapterFixture _fixture;
    public RingOnlyCallTests(AdapterFixture fixture) => _fixture = fixture;

    [Fact]
    public void Ring_only_declares_invitation_support_without_audio()
    {
        var capability = new UnavailableMediaEngine(ringOnly: true).Capability;
        Assert.True(capability.RingOnly);
        Assert.True(capability.HasProtocolProposal);
        Assert.False(capability.Available);
        Assert.False(capability.Audio);
        Assert.False(capability.Video);
        Assert.Equal("ring_only", capability.Engine);
        Assert.False(new UnavailableMediaEngine().Capability.RingOnly);
        Assert.False(new UnavailableMediaEngine().Capability.HasProtocolProposal);
    }

    [Fact]
    public async Task Configured_host_exposes_ring_only_control_without_media()
    {
        await using var app = Nyarugram.Milky.Host.Program.CreateApp(new Nyarugram.Milky.Host.MilkyOptions
        {
            BackendBaseAddress = new Uri("https://127.0.0.1:59999"),
            ServerDomain = "example.local",
            ConnectionString = "Host=127.0.0.1;Database=unused",
            SecretKey = Convert.ToBase64String(new byte[32]),
            ListenUrl = "http://127.0.0.1:0",
            AccessToken = "isolated-test-access-token",
            MediaEngine = "ring_only",
        }, _fixture.App.Environment.ContentRootPath);
        var engine = app.Services.GetRequiredService<IMediaEngine>();
        Assert.True(engine.Capability.RingOnly);
        Assert.False(engine.Capability.Available);
        var actions = app.Services.GetRequiredService<MilkyActions>();
        var capabilities = Assert.IsType<NyarugramCapabilities>(await actions.HandleAsync(
            "nyarugram_get_capabilities", default, CancellationToken.None));
        Assert.True(capabilities.Calls.Control);
        Assert.True(capabilities.Calls.RingOnly);
        Assert.False(capabilities.Calls.Voice);
        Assert.False(capabilities.Calls.Video);
    }

    [SkippableTheory]
    [InlineData(P.PhoneCallState.Accepted, P.PhoneCallDiscardReason.Unspecified, "hangup")]
    [InlineData(P.PhoneCallState.Discarded, P.PhoneCallDiscardReason.Busy, "busy")]
    [InlineData(P.PhoneCallState.Discarded, P.PhoneCallDiscardReason.Missed, "missed")]
    public async Task Ring_only_invites_end_and_allow_redial(
        P.PhoneCallState state, P.PhoneCallDiscardReason reason, string expectedReason)
    {
        if (_fixture.StartupError is not null) throw new SkipException("Isolated PostgreSQL unavailable");
        var engine = new UnavailableMediaEngine(ringOnly: true);
        await using var service = ActivatorUtilities.CreateInstance<CallService>(_fixture.App.Services, engine);
        var context = _fixture.Runtime.Require();
        var input = new StartCallInput
        {
            UserId = FakeBackend.FriendUin, RequestId = "ring-" + Guid.NewGuid().ToString("N"),
        };
        var started = await service.StartAsync(context, input, CancellationToken.None);
        var id = Guid.Parse(started.CallId);
        var record = await _fixture.Get<CallStore>().GetAsync(context.Account.AccountId, id, CancellationToken.None);
        Assert.NotNull(record?.BackendCallId);
        var backendId = record!.BackendCallId!.Value;
        try
        {
            var replay = await service.StartAsync(context, input, CancellationToken.None);
            Assert.Equal(started.CallId, replay.CallId);
            _fixture.Backend.AdvanceCall(backendId, state == P.PhoneCallState.Accepted ? "accepted" : "ended", reason);
            await service.ApplyAsync(context, new P.PhoneCall
            {
                CallId = backendId, AccessHash = record.AccessHash ?? 0,
                State = state, DiscardReason = reason,
            }, CancellationToken.None);
            var ended = await service.GetAsync(context, started.CallId, CancellationToken.None);
            Assert.Equal(CallStates.Ended, ended.State);
            Assert.Equal(expectedReason, ended.EndReason);
            Assert.Equal("ended", _fixture.Backend.GetCall(backendId).State);
            Assert.NotEqual("ready", ended.MediaState);
            Assert.Null(engine.Find(id));
            Assert.Empty(_fixture.Backend.GetCall(backendId).Ga);

            var redial = await service.StartAsync(context, new StartCallInput
            {
                UserId = FakeBackend.FriendUin, RequestId = "redial-" + Guid.NewGuid().ToString("N"),
            }, CancellationToken.None);
            Assert.NotEqual(started.CallId, redial.CallId);
            await service.EndAsync(context, new EndCallInput
            {
                CallId = redial.CallId, Reason = "cancel",
            }, CancellationToken.None);
        }
        finally
        {
            await service.EndAsync(context, new EndCallInput { CallId = started.CallId, Reason = "cancel" }, CancellationToken.None);
        }
    }
}