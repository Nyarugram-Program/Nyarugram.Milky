using System.Collections.Concurrent;
using System.Threading.Channels;
using Nyarugram.Milky.Core;

namespace Nyarugram.Milky.Media;

/// <summary>
/// Deterministic loopback engine used for host tests and for exercising the application
/// media contract. It echoes application audio back to the same connection. It has no
/// remote peer, performs no telephony signalling and must never be reported as voice.
/// </summary>
public sealed class LoopbackMediaEngine : IMediaEngine
{
    private readonly ConcurrentDictionary<Guid, LoopbackSession> _sessions = new();

    /// <summary>
    /// Loopback advertises a syntactically valid protocol so the control plane can be
    /// exercised end to end against a real backend. It has no real transport, so a real
    /// client negotiates no common library version and audio is never claimed.
    /// </summary>
    public MediaCapability Capability { get; } = new(
        Available: true,
        Audio: false,
        Video: false,
        Engine: "loopback",
        Reason: "loopback engine: application audio is echoed locally; no remote party and no telephony transport",
        UdpP2P: false,
        UdpReflector: false,
        MinLayer: 65,
        MaxLayer: 92,
        LibraryVersions: ["loopback-1"],
        // The loopback contract is "echo immediately"; that guarantee is covered by a test
        // and applies only to this test engine.
        ReadyOnStart: true);

    public Task<IMediaSession> StartAsync(
        MediaSessionRequest request, Func<byte[], Task> onSignaling, CancellationToken ct)
    {
        var session = new LoopbackSession(request.CallId, onSignaling);
        _sessions[request.CallId] = session;
        return Task.FromResult<IMediaSession>(session);
    }

    public Task OnSignalingAsync(Guid callId, byte[] payload, CancellationToken ct) => Task.CompletedTask;

    public IMediaSession? Find(Guid callId)
        => _sessions.TryGetValue(callId, out var session) ? session : null;

    /// <summary>
    /// Host-test hook: simulates an abnormal engine termination for one session so the
    /// call-control failure path can be exercised without a native agent.
    /// </summary>
    public bool FailSession(Guid callId, string reason)
        => _sessions.TryGetValue(callId, out var session) && session.Fail(reason);

    /// <summary>Host-test hook: emits an engine signal through the real callback path.</summary>
    public async Task<bool> EmitSignalingAsync(Guid callId, byte[] payload)
    {
        if (!_sessions.TryGetValue(callId, out var session)) return false;
        await session.EmitSignalingAsync(payload).ConfigureAwait(false);
        return true;
    }

    public void Remove(Guid callId)
    {
        if (_sessions.TryRemove(callId, out var session)) _ = session.DisposeAsync();
    }
}

public sealed class LoopbackSession : IMediaSession, IApplicationAudioChannel
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly TaskCompletionSource<MediaFault?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<byte[], Task>? _onSignaling;
    private volatile string _state = "active";

    public LoopbackSession(Guid callId, Func<byte[], Task>? onSignaling = null)
    {
        CallId = callId;
        _onSignaling = onSignaling;
    }

    public Guid CallId { get; }
    public string State => _state;
    public IApplicationAudioChannel? ApplicationAudio => this;

    public Task SendSignalingAsync(byte[] data, CancellationToken ct) => Task.CompletedTask;

    public Task WaitForReadyAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Null for an intentional close, a fault for an abnormal termination.</summary>
    public Task<MediaFault?> Completion => _completion.Task;

    /// <summary>
    /// Host-test hook: reports an abnormal termination the way a real agent crash would.
    /// Idempotent: only the first fault (or the intentional close) is published.
    /// </summary>
    public bool Fail(string reason)
    {
        if (!_completion.TrySetResult(new MediaFault("engine_failed", reason))) return false;
        _state = "failed";
        _output.Writer.TryComplete();
        return true;
    }

    /// <summary>Host-test hook: emits a signal through the callback captured at start.</summary>
    public Task EmitSignalingAsync(byte[] payload)
        => _onSignaling?.Invoke(payload) ?? Task.CompletedTask;

    /// <summary>Echoes an input frame back to the application as output.</summary>
    public void WriteApplicationAudio(AudioFrame frame)
    {
        if (_state != "active") return;
        _output.Writer.TryWrite(new AudioFrame(false, frame.Sequence, frame.TimestampMs, frame.Payload));
    }

    public IAsyncEnumerable<AudioFrame> ReadPeerAudioAsync(CancellationToken ct)
        => _output.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync()
    {
        _completion.TrySetResult(null);
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
