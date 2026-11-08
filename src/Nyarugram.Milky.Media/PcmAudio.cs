using Nyarugram.Milky.Core;

namespace Nyarugram.Milky.Media;

/// <summary>
/// Application-facing audio format: signed 16-bit little-endian PCM, 48 kHz, mono,
/// 20 ms per frame (1920 payload bytes). Header layout is fixed and versioned:
///
///   0..1   magic 'N','M'
///   2      version (1)
///   3      flags (bit0 = input audio from the application)
///   4..7   sequence (uint32, big-endian)
///   8..15  timestamp in milliseconds (uint64, big-endian)
///   16..   PCM payload
/// </summary>
public static class PcmAudio
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int FrameMs = 20;
    public const int FrameSamples = SampleRate * FrameMs / 1000;
    public const int PayloadBytes = FrameSamples * Channels * BitsPerSample / 8;
    public const int HeaderBytes = 16;
    public const int MaxFrameBytes = HeaderBytes + PayloadBytes;
    public const byte Version = 1;
    public const byte MagicN = (byte)'N';
    public const byte MagicM = (byte)'M';

    public static byte[] BuildFrame(uint sequence, ulong timestampMs, bool input, ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > PayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"payload must be 1..{PayloadBytes} bytes");
        var frame = new byte[HeaderBytes + payload.Length];
        frame[0] = MagicN;
        frame[1] = MagicM;
        frame[2] = Version;
        frame[3] = input ? (byte)1 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(8, 8), timestampMs);
        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static bool TryParse(ReadOnlySpan<byte> frame, out AudioFrame parsed, out string error)
    {
        parsed = default;
        error = "";
        if (frame.Length < HeaderBytes)
        {
            error = "frame shorter than the 16-byte header";
            return false;
        }
        if (frame[0] != MagicN || frame[1] != MagicM)
        {
            error = "bad magic";
            return false;
        }
        if (frame[2] != Version)
        {
            error = $"unsupported frame version {frame[2]}";
            return false;
        }
        var payload = frame[HeaderBytes..];
        if (payload.Length > PayloadBytes)
        {
            error = "payload exceeds one 20 ms frame";
            return false;
        }
        if (payload.Length % (Channels * BitsPerSample / 8) != 0)
        {
            error = "payload is not a whole number of samples";
            return false;
        }
        parsed = new AudioFrame(
            (frame[3] & 1) != 0,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame[4..8]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(frame[8..16]),
            payload.ToArray());
        return true;
    }

    public static byte[] Silence() => new byte[PayloadBytes];
}
