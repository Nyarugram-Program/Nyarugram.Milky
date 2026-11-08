using Concentus;
using Concentus.Enums;

namespace Nyarugram.Milky.Media;

/// <summary>
/// Opus codec bridge for the 48 kHz mono 20 ms application frames. Used by the media
/// pipeline; it is verified in host tests only and is not evidence of interoperability
/// with the Android telephony engine.
/// </summary>
public sealed class OpusCodec : IDisposable
{
    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private readonly byte[] _encodeBuffer = new byte[512];

    public OpusCodec()
    {
        _encoder = OpusCodecFactory.CreateEncoder(PcmAudio.SampleRate, PcmAudio.Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 24000;
        _decoder = OpusCodecFactory.CreateDecoder(PcmAudio.SampleRate, PcmAudio.Channels);
    }

    public byte[] Encode(ReadOnlySpan<byte> pcm)
    {
        var samples = ToSamples(pcm);
        if (samples.Length != PcmAudio.FrameSamples)
            throw new ArgumentException($"expected {PcmAudio.FrameSamples} samples", nameof(pcm));
        var written = _encoder.Encode(samples.AsSpan(), samples.Length, _encodeBuffer.AsSpan(), _encodeBuffer.Length);
        return _encodeBuffer.AsSpan(0, written).ToArray();
    }

    public byte[] Decode(ReadOnlySpan<byte> opus)
    {
        var samples = new short[PcmAudio.FrameSamples];
        var decoded = _decoder.Decode(opus, samples.AsSpan(), PcmAudio.FrameSamples);
        var pcm = new byte[decoded * 2];
        for (var i = 0; i < decoded; i++)
        {
            pcm[i * 2] = (byte)(samples[i] & 0xff);
            pcm[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xff);
        }
        return pcm;
    }

    private static short[] ToSamples(ReadOnlySpan<byte> pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
        return samples;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
