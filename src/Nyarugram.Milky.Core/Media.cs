namespace Nyarugram.Milky.Core;

/// <summary>
/// Diffie-Hellman primitives required by the Nyarugram call handshake. Implementations
/// must use a cryptographically secure random source and big-integer arithmetic.
/// </summary>
public interface IDiffieHellman
{
    byte[] RandomSecret(int bytes);

    /// <summary>Computes g^secret mod p as big-endian bytes.</summary>
    byte[] PublicValue(byte[] prime, int generator, byte[] secret);

    /// <summary>Computes other^secret mod p as big-endian bytes.</summary>
    byte[] SharedSecret(byte[] prime, byte[] otherPublic, byte[] secret);

    byte[] Sha256(byte[] value);

    /// <summary>Telegram call key fingerprint: the low 64 bits of SHA-1, signed.</summary>
    long Fingerprint(byte[] key);
}

/// <summary>One application audio frame, independent of the wire framing.</summary>
public readonly record struct AudioFrame(bool Input, uint Sequence, ulong TimestampMs, byte[] Payload);

/// <summary>
/// An unexpected end of a media session. A normal, caller-initiated close completes
/// <see cref="IMediaSession.Completion"/> with <c>null</c> instead.
/// </summary>
public sealed record MediaFault(string Reason, string? Detail = null);

public sealed record MediaCapability(
    bool Available,
    bool Audio,
    bool Video,
    string Engine,
    string? Reason,
    bool UdpP2P = false,
    bool UdpReflector = false,
    int MinLayer = 0,
    int MaxLayer = 0,
    IReadOnlyList<string>? LibraryVersions = null,
    bool ReadyOnStart = false)
{
    /// <summary>Protocol proposal is only valid when the engine declares real layers.</summary>
    public bool HasProtocolProposal => MinLayer > 0 && MaxLayer >= MinLayer && LibraryVersions is { Count: > 0 };
}

public sealed record MediaEndpoint(
    long Id, string Ip, string Ipv6, int Port, bool Turn, bool Stun, string Username, string Password);

/// <summary>
/// Everything an engine needs to open media for one call. The shared key is the raw DH
/// result; engines that need a different derivation do it themselves.
/// </summary>
public sealed record MediaSessionRequest(
    Guid CallId,
    long PeerUin,
    bool Video,
    bool Caller,
    byte[] SharedKey,
    IReadOnlyList<MediaEndpoint> Endpoints,
    string EngineConfigJson,
    bool P2PAllowed,
    IReadOnlyList<string>? LibraryVersions = null);

/// <summary>Application-facing audio channel; only engines that expose it are opened to apps.</summary>
public interface IApplicationAudioChannel
{
    /// <summary>Audio produced by the application for the remote party.</summary>
    void WriteApplicationAudio(AudioFrame frame);

    /// <summary>Audio received from the remote party for the application.</summary>
    IAsyncEnumerable<AudioFrame> ReadPeerAudioAsync(CancellationToken ct);
}

public interface IMediaSession : IAsyncDisposable
{
    Guid CallId { get; }
    string State { get; }

    /// <summary>Non-null only when this session can carry application audio.</summary>
    IApplicationAudioChannel? ApplicationAudio { get; }

    /// <summary>Opaque signaling (JSON) that must be forwarded to the peer via the backend.</summary>
    Task SendSignalingAsync(byte[] data, CancellationToken ct);

    /// <summary>
    /// Completes when the session can actually carry audio to and from the peer. Only an
    /// engine whose contract guarantees immediate readiness may return synchronously; a
    /// plain "StartAsync returned" is not treated as media ready.
    /// </summary>
    Task WaitForReadyAsync(CancellationToken ct);

    /// <summary>
    /// Completes exactly once when the session stops being able to carry audio: <c>null</c>
    /// for an intentional close, a <see cref="MediaFault"/> for an unexpected termination.
    /// A fault must not wait for the owner to dispose the session.
    /// </summary>
    Task<MediaFault?> Completion { get; }
}

/// <summary>
/// Pluggable media engine. The default registration reports
/// <see cref="MediaCapability.Available"/> = false; a real tgcalls engine is provided by
/// the separate native media agent and only advertised when it is configured.
/// </summary>
public interface IMediaEngine
{
    MediaCapability Capability { get; }

    Task<IMediaSession> StartAsync(
        MediaSessionRequest request, Func<byte[], Task> onSignaling, CancellationToken ct);

    /// <summary>Feeds opaque signaling bytes produced by the backend into the engine.</summary>
    Task OnSignalingAsync(Guid callId, byte[] payload, CancellationToken ct);

    /// <summary>Live session for a call, if this process still owns one.</summary>
    IMediaSession? Find(Guid callId);
}

/// <summary>Default engine: no media transport is linked into this build.</summary>
public sealed class UnavailableMediaEngine : IMediaEngine
{
    public MediaCapability Capability { get; } = new(
        Available: false,
        Audio: false,
        Video: false,
        Engine: "none",
        Reason: "no tgcalls-compatible engine is linked into this build; real bidirectional audio is not verified");

    public Task<IMediaSession> StartAsync(
        MediaSessionRequest request, Func<byte[], Task> onSignaling, CancellationToken ct)
        => throw new MilkyActionException(MilkyErrorCode.MediaUnavailable, Capability.Reason!);

    public Task OnSignalingAsync(Guid callId, byte[] payload, CancellationToken ct) => Task.CompletedTask;

    public IMediaSession? Find(Guid callId) => null;
}
