using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Media;
using Xunit;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Exercises the real tgcalls engine through the native media agent. The agent is a
/// separate process built from the same tgcalls sources as the Android client and is
/// only available where it has been built, so the test skips instead of faking a pass.
///
/// Set NYARUGRAM_MEDIA_AGENT_COMMAND to the agent command, for example:
///   wsl -d Ubuntu-24.04 -- /mnt/d/BotgramBuild/milky-media/build/milky_media_agent --agent
/// </summary>
public sealed class TgcallsEngineTests
{
    private const int SampleRate = 48000;
    private const int FrameSamples = 960;

    [SkippableFact]
    public async Task Two_agents_establish_a_real_p2p_call_with_full_duplex_audio()
    {
        var command = Environment.GetEnvironmentVariable("NYARUGRAM_MEDIA_AGENT_COMMAND");
        Skip.If(string.IsNullOrWhiteSpace(command), "NYARUGRAM_MEDIA_AGENT_COMMAND is not set");

        var key = new byte[256];
        Random.Shared.NextBytes(key);
        var callerId = Guid.NewGuid();
        var calleeId = Guid.NewGuid();

        TgcallsMediaEngine? callerEngine = null;
        TgcallsMediaEngine? calleeEngine = null;
        IMediaSession? caller = null;
        IMediaSession? callee = null;
        try
        {
            callerEngine = new TgcallsMediaEngine(command, TimeSpan.FromSeconds(15));
            calleeEngine = new TgcallsMediaEngine(command, TimeSpan.FromSeconds(15));
            Assert.True(callerEngine.Capability.Available);
            Assert.True(callerEngine.Capability.Audio);

            var callerAudio = Channel.CreateUnbounded<AudioFrame>();
            var calleeAudio = Channel.CreateUnbounded<AudioFrame>();

            caller = await callerEngine.StartAsync(
                Request(callerId, caller: true, key), Forward(calleeEngine, calleeId), CancellationToken.None);
            callee = await calleeEngine.StartAsync(
                Request(calleeId, caller: false, key), Forward(callerEngine, callerId), CancellationToken.None);

            Assert.NotNull(caller.ApplicationAudio);
            Assert.NotNull(callee.ApplicationAudio);

            var callerReader = Pump(caller, callerAudio.Writer);
            var calleeReader = Pump(callee, calleeAudio.Writer);

            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await caller.WaitForReadyAsync(ready.Token);
            await callee.WaitForReadyAsync(ready.Token);

            var callerPhase = 0;
            var calleePhase = 0;
            var stopFeeding = new CancellationTokenSource();
            var feeder = Task.Run(async () =>
            {
                while (!stopFeeding.IsCancellationRequested)
                {
                    caller.ApplicationAudio!.WriteApplicationAudio(Frame(440.0, ref callerPhase));
                    callee.ApplicationAudio!.WriteApplicationAudio(Frame(660.0, ref calleePhase));
                    await Task.Delay(20);
                }
            });

            var callerReceived = await CollectAsync(callerAudio.Reader, 80, TimeSpan.FromSeconds(8));
            var calleeReceived = await CollectAsync(calleeAudio.Reader, 80, TimeSpan.FromSeconds(8));
            stopFeeding.Cancel();
            await feeder;

            Assert.True(calleeReceived.Count >= 40, $"callee received only {calleeReceived.Count} frames");
            Assert.True(callerReceived.Count >= 40, $"caller received only {callerReceived.Count} frames");
            Assert.True(Rms(calleeReceived) > 100, $"callee RMS {Rms(calleeReceived):F1}");
            Assert.True(Rms(callerReceived) > 100, $"caller RMS {Rms(callerReceived):F1}");

            callerReader.Cancel();
            calleeReader.Cancel();
        }
        finally
        {
            if (caller is not null) await caller.DisposeAsync();
            if (callee is not null) await callee.DisposeAsync();
        }
    }

    private static MediaSessionRequest Request(Guid callId, bool caller, byte[] key)
        => new(callId, PeerUin: 100002, Video: false, Caller: caller, SharedKey: key,
            Endpoints: Array.Empty<MediaEndpoint>(), EngineConfigJson: "", P2PAllowed: true,
            LibraryVersions: ["13.0.0", "12.0.0"]);

    private static Func<byte[], Task> Forward(IMediaEngine target, Guid targetCall)
        => payload => target.OnSignalingAsync(targetCall, payload, CancellationToken.None);

    private static AudioFrame Frame(double frequency, ref int phase)
    {
        var samples = new short[FrameSamples];
        int start = phase;
        for (int i = 0; i < FrameSamples; i++)
        {
            samples[i] = (short)(3000.0 * Math.Sin(2.0 * Math.PI * frequency * (start + i) / SampleRate));
        }
        phase += FrameSamples;
        var payload = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            payload[i * 2] = (byte)(samples[i] & 0xFF);
            payload[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return new AudioFrame(true, 0, 0, payload);
    }

    private static CancellationTokenSource Pump(IMediaSession session, ChannelWriter<AudioFrame> writer)
    {
        var cancel = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in session.ApplicationAudio!.ReadPeerAudioAsync(cancel.Token))
                    writer.TryWrite(frame);
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
        });
        return cancel;
    }

    private static async Task<List<AudioFrame>> CollectAsync(
        ChannelReader<AudioFrame> reader, int minimum, TimeSpan timeout)
    {
        var frames = new List<AudioFrame>();
        var deadline = Stopwatch.StartNew();
        while (frames.Count < minimum && deadline.Elapsed < timeout)
        {
            using var wait = new CancellationTokenSource(timeout - deadline.Elapsed);
            if (wait.IsCancellationRequested) break;
            try { frames.Add(await reader.ReadAsync(wait.Token)); }
            catch (OperationCanceledException) { break; }
        }
        return frames;
    }

    private static double Rms(IReadOnlyList<AudioFrame> frames)
    {
        double sum = 0;
        long count = 0;
        foreach (var frame in frames)
        {
            for (int i = 0; i + 1 < frame.Payload.Length; i += 2)
            {
                short sample = (short)(frame.Payload[i] | (frame.Payload[i + 1] << 8));
                sum += (double)sample * sample;
                count++;
            }
        }
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }
}
