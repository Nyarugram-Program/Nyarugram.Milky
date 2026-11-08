using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nyarugram.Milky.Core;

namespace Nyarugram.Milky.Media;

/// <summary>
/// Real tgcalls engine hosted in a separate native process.
///
/// The process is the Android client's own <c>InstanceV2Impl</c> built for the host
/// platform. The adapter never reimplements the call protocol: it supplies the
/// negotiated DH key, backend endpoints and engine configuration, forwards opaque
/// signaling, and exchanges PCM audio over the agent's framed control socket.
/// A process is used per call so a crash or hung engine cannot take down the
/// adapter, and so the engine's global state cannot leak between calls.
/// </summary>
public sealed class TgcallsMediaEngine : IMediaEngine
{
    private static readonly string[] SupportedVersions = ["13.0.0", "12.0.0"];

    private readonly ConcurrentDictionary<Guid, TgcallsSession> _sessions = new();
    private readonly string? _command;
    private readonly TimeSpan _startupTimeout;
    private readonly bool _agentLog;

    public TgcallsMediaEngine(string? agentCommand, TimeSpan startupTimeout, bool agentLog = false)
    {
        _command = string.IsNullOrWhiteSpace(agentCommand) ? null : agentCommand.Trim();
        _startupTimeout = startupTimeout;
        _agentLog = agentLog;
        Capability = _command is null
            ? new MediaCapability(
                Available: false, Audio: false, Video: false, Engine: "tgcalls",
                Reason: "media.agent_command is not configured; the native tgcalls agent is not available")
            : new MediaCapability(
                Available: true, Audio: true, Video: false, Engine: "tgcalls-13.0.0",
                Reason: null, UdpP2P: true, UdpReflector: true,
                MinLayer: 65, MaxLayer: 92, LibraryVersions: SupportedVersions);
    }

    public MediaCapability Capability { get; }

    public async Task<IMediaSession> StartAsync(
        MediaSessionRequest request, Func<byte[], Task> onSignaling, CancellationToken ct)
    {
        if (_command is null)
            throw new MilkyActionException(MilkyErrorCode.MediaUnavailable, Capability.Reason!);

        var version = PickVersion(request.LibraryVersions);
        if (version is null)
        {
            throw new MilkyActionException(
                MilkyErrorCode.MediaUnavailable,
                "no common tgcalls protocol version between the engine and the negotiated call");
        }

        var session = await TgcallsSession.StartAsync(
            request, version, _command, _startupTimeout, _agentLog, onSignaling, Remove, ct).ConfigureAwait(false);
        if (_sessions.TryGetValue(request.CallId, out var previous))
            _ = previous.DisposeAsync();
        _sessions[request.CallId] = session;
        return session;
    }

    public async Task OnSignalingAsync(Guid callId, byte[] payload, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(callId, out var session)) return;
        if (session.IsDisposed)
        {
            Remove(callId);
            return;
        }
        try
        {
            await session.SendSignalingAsync(payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The control channel is broken. Publishing a fault is what lets CallService end
            // the call; dropping the signal silently would leave it observably ready while
            // no engine is running.
            session.FailTransport("signaling_write_failed");
        }
    }

    public IMediaSession? Find(Guid callId)
        => _sessions.TryGetValue(callId, out var session) ? session : null;

    public void Remove(Guid callId) => _sessions.TryRemove(callId, out _);

    private static string? PickVersion(IReadOnlyList<string>? negotiated)
    {
        if (negotiated is { Count: > 0 })
        {
            foreach (var candidate in negotiated)
            {
                if (SupportedVersions.Contains(candidate, StringComparer.Ordinal))
                    return candidate;
            }
            return null;
        }
        return SupportedVersions[0];
    }
}

internal sealed class TgcallsSession : IMediaSession, IApplicationAudioChannel
{
    private const byte KindControl = 0x01;
    private const byte KindAudioToEngine = 0x02;
    private const byte KindAudioFromEngine = 0x03;
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Process _process;
    private readonly string _version;
    private readonly Func<byte[], Task> _onSignaling;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<MediaFault?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<AudioFrame> _peerAudio;
    private readonly Channel<byte[]> _appAudio;
    private readonly Task _reader;
    private readonly Task _writer;
    private readonly Action<Guid> _onClosed;
    private readonly string _stderrTail = "";
    private long _sequence;
    private volatile string _state = "starting";
    private volatile bool _established;
    private volatile int _peerFrames;
    private volatile bool _disposed;

    private TgcallsSession(
        Guid callId, string version, TcpClient client, Process process,
        Func<byte[], Task> onSignaling, Action<Guid> onClosed, string stderrTail)
    {
        CallId = callId;
        _version = version;
        _client = client;
        _stream = client.GetStream();
        _process = process;
        _onSignaling = onSignaling;
        _onClosed = onClosed;
        _stderrTail = stderrTail;
        _peerAudio = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });
        _appAudio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _reader = Task.Run(ReadLoopAsync);
        _writer = Task.Run(WriteLoopAsync);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => { try { _client.Close(); } catch { /* unblocks the read loop */ } };
        if (_process.HasExited) { try { _client.Close(); } catch { /* already gone */ } }
    }

    public Guid CallId { get; }
    public string State => _state;
    public bool IsDisposed => _disposed;
    public IApplicationAudioChannel? ApplicationAudio => this;
    public Task<MediaFault?> Completion => _completion.Task;

    public static async Task<TgcallsSession> StartAsync(
        MediaSessionRequest request, string version, string command, TimeSpan startupTimeout,
        bool agentLog, Func<byte[], Task> onSignaling, Action<Guid> onClosed, CancellationToken ct)
    {
        var (file, arguments) = SplitCommand(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new MilkyActionException(MilkyErrorCode.Transient, "media agent failed to start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new MilkyActionException(
                MilkyErrorCode.MediaUnavailable, $"media agent could not be started: {ex.Message}");
        }

        var stderr = Drain(process);
        try
        {
            var firstLine = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(startupTimeout, ct).ConfigureAwait(false);
            if (firstLine is null || !firstLine.StartsWith("READY ", StringComparison.Ordinal))
                throw new MilkyActionException(
                    MilkyErrorCode.MediaUnavailable,
                    $"media agent did not report a control port: {firstLine ?? "<no output>"} {stderr.Tail()}");
            int port = int.Parse(firstLine[6..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(startupTimeout, ct)
                .ConfigureAwait(false);
            var session = new TgcallsSession(
                request.CallId, version, client, process, onSignaling, onClosed, stderr.Tail());
            await session.SendStartAsync(request, version, agentLog, ct).ConfigureAwait(false);
            await session._started.Task.WaitAsync(startupTimeout, ct).ConfigureAwait(false);

            // A line printer keeps draining later output; no engine output is user-visible.
            _ = process.StandardOutput.ReadToEndAsync();
            return session;
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignored */ }
            process.Dispose();
            throw;
        }
    }

    private async Task SendStartAsync(MediaSessionRequest request, string version, bool agentLog, CancellationToken ct)
    {
        var servers = new List<Dictionary<string, object>>();
        foreach (var endpoint in request.Endpoints)
        {
            string host = endpoint.Ip.Length > 0 ? endpoint.Ip : endpoint.Ipv6;
            if (host.Length == 0 || endpoint.Port is < 1 or > 65535) continue;
            servers.Add(new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = endpoint.Port,
                ["username"] = endpoint.Turn ? endpoint.Username : "",
                ["password"] = endpoint.Turn ? endpoint.Password : "",
                ["turn"] = endpoint.Turn,
            });
        }

        var start = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "start",
            ["version"] = version,
            ["caller"] = request.Caller,
            ["p2p"] = request.P2PAllowed,
            ["shared_key_b64"] = Convert.ToBase64String(request.SharedKey),
            ["custom_parameters"] = request.EngineConfigJson,
            ["rtc_servers"] = servers,
            ["log"] = agentLog,
        });
        await WriteMessageAsync(KindControl, Encoding.UTF8.GetBytes(start), ct).ConfigureAwait(false);
    }

    public async Task SendSignalingAsync(byte[] data, CancellationToken ct)
    {
        if (_disposed) return;
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "signaling",
            ["data"] = Convert.ToBase64String(data),
        });
        await WriteMessageAsync(KindControl, Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
    }

    public Task WaitForReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    /// <summary>Ends the session after a transport failure detected by the owner.</summary>
    internal void FailTransport(string reason) => Fail(reason);

    public void WriteApplicationAudio(AudioFrame frame)
    {
        if (_disposed || frame.Payload.Length == 0 || frame.Payload.Length > 1920 || frame.Payload.Length % 2 != 0)
            return;
        _appAudio.Writer.TryWrite(frame.Payload);
    }

    public IAsyncEnumerable<AudioFrame> ReadPeerAudioAsync(CancellationToken ct)
        => _peerAudio.Reader.ReadAllAsync(ct);

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var payload in _appAudio.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                await WriteMessageAsync(KindAudioToEngine, payload, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(_shutdown.Token).ConfigureAwait(false);
                if (message is null)
                {
                    if (!_disposed) Fail("agent_disconnected");
                    break;
                }
                var kind = message.Value.Kind;
                var payload = message.Value.Payload;
                if (kind == KindAudioFromEngine)
                {
                    if (payload.Length is < 2 or > 1920 || payload.Length % 2 != 0) continue;
                    _peerFrames++;
                    _peerAudio.Writer.TryWrite(new AudioFrame(
                        false, (uint)Interlocked.Increment(ref _sequence), (ulong)Environment.TickCount64, payload));
                    MaybeReady();
                    continue;
                }
                if (kind != KindControl) continue;
                await HandleControlAsync(payload).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) Fail(ex.Message);
        }
        finally
        {
            _peerAudio.Writer.TryComplete();
        }
    }

    private async Task HandleControlAsync(byte[] payload)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { return; }
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        switch (type)
        {
            case "started":
                _started.TrySetResult(true);
                break;
            case "state":
                _state = root.TryGetProperty("state", out var state) ? state.GetString() ?? _state : _state;
                if (_state == "established") _established = true;
                if (_state == "failed") Fail("engine_failed");
                MaybeReady();
                break;
            case "signaling":
                if (root.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } encoded)
                {
                    try { await _onSignaling(Convert.FromBase64String(encoded)).ConfigureAwait(false); }
                    catch { /* the adapter enqueues into its bounded relay; a callback failure must not kill the media read loop */ }
                }
                break;
            case "failed":
                Fail(root.TryGetProperty("code", out var code) ? code.GetString() ?? "engine_failed" : "engine_failed");
                break;
            case "error":
                Fail(root.TryGetProperty("code", out var error) ? error.GetString() ?? "engine_error" : "engine_error");
                break;
            case "stopped":
                _state = "stopped";
                if (!_disposed) Fail("engine_stopped");
                break;
        }
    }

    private void MaybeReady()
    {
        if (_established && _peerFrames > 0)
            _ready.TrySetResult(true);
    }

    /// <summary>
    /// Ends the session abnormally. This is called from the read/write loops or the
    /// process-exit handler and therefore must never await the loops or the owner's
    /// disposal: it publishes the fault, fails any pending start/ready wait, cancels
    /// the transport and releases the process. Disposal later is a no-op except for
    /// final cleanup.
    /// </summary>
    private void Fail(string reason)
    {
        if (_disposed || _completion.Task.IsCompleted) return;
        _state = "failed";
        var detail = reason + (_stderrTail.Length > 0 ? " " + _stderrTail : "");
        var error = new MilkyActionException(
            MilkyErrorCode.MediaUnavailable, $"media agent error: {detail}");
        _completion.TrySetResult(new MediaFault("media_agent_failed", detail));
        _started.TrySetException(error);
        _ready.TrySetException(error);
        _shutdown.Cancel();
        _appAudio.Writer.TryComplete();
        _peerAudio.Writer.TryComplete();
        try { _client.Close(); } catch { /* best effort */ }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private async Task<(byte Kind, byte[] Payload)?> ReadMessageAsync(CancellationToken ct)
    {
        var header = new byte[5];
        int read = 0;
        while (read < header.Length)
        {
            int got = await _stream.ReadAsync(header.AsMemory(read, header.Length - read), ct).ConfigureAwait(false);
            if (got == 0) return null;
            read += got;
        }
        uint length = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
        if (length == 0 || length > MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"invalid agent frame length={length} kind={header[4]} bytes={string.Join(',', header)} " +
                $"exited={_process.HasExited} code={(_process.HasExited ? _process.ExitCode : 0)}");
        }
        var payload = new byte[length];
        read = 0;
        while (read < payload.Length)
        {
            int got = await _stream.ReadAsync(payload.AsMemory(read), ct).ConfigureAwait(false);
            if (got == 0) return null;
            read += got;
        }
        return (header[4], payload);
    }

    private async Task WriteMessageAsync(byte Kind, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[5 + payload.Length];
        uint length = (uint)payload.Length;
        frame[0] = (byte)((length >> 24) & 0xFF);
        frame[1] = (byte)((length >> 16) & 0xFF);
        frame[2] = (byte)((length >> 8) & 0xFF);
        frame[3] = (byte)(length & 0xFF);
        frame[4] = Kind;
        payload.CopyTo(frame, 5);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // An intentional close is not a fault; the owner's watcher must ignore it.
        _completion.TrySetResult(null);
        _onClosed(CallId);
        _appAudio.Writer.TryComplete();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await WriteMessageAsync(KindControl, Encoding.UTF8.GetBytes("{\"type\":\"stop\"}"), timeout.Token)
                .ConfigureAwait(false);
        }
        catch { /* best effort */ }
        try
        {
            if (!_process.HasExited)
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token)
                    .ConfigureAwait(false);
        }
        catch { /* ignored */ }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* ignored */ }
        _shutdown.Cancel();
        try { _client.Close(); } catch { /* ignored */ }
        try { await Task.WhenAny(Task.WhenAll(_reader, _writer), Task.Delay(500)).ConfigureAwait(false); }
        catch { /* ignored */ }
        _process.Dispose();
        _ready.TrySetException(new MilkyActionException(MilkyErrorCode.MediaUnavailable, "media session closed"));
        _peerAudio.Writer.TryComplete();
    }

    private static (string File, string Arguments) SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (var c in command)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        if (parts.Count == 0) throw new InvalidOperationException("media.agent_command is empty");
        return (parts[0], string.Join(' ', parts.Skip(1).Select(QuoteIfNeeded)));
    }

    private static string QuoteIfNeeded(string part)
        => part.Contains(' ') ? $"\"{part}\"" : part;

    private static StderrTail Drain(Process process)
    {
        var tail = new StderrTail();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) return;
                    tail.Add(line);
                }
            }
            catch { /* process exited */ }
        });
        return tail;
    }

    private sealed class StderrTail
    {
        private readonly Queue<string> _lines = new();
        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > 4) _lines.Dequeue();
            }
        }
        public string Tail()
        {
            lock (_lines) return string.Join(" | ", _lines);
        }
    }
}
