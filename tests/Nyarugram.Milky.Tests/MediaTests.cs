using System.Security.Cryptography;
using System.Text;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Media;
using Xunit;

namespace Nyarugram.Milky.Tests;

public sealed class PcmAudioTests
{
    [Fact]
    public void Frame_roundtrips_with_header_and_payload()
    {
        var payload = new byte[PcmAudio.PayloadBytes];
        Random.Shared.NextBytes(payload);

        var frame = PcmAudio.BuildFrame(7, 1234, input: true, payload);

        Assert.Equal(PcmAudio.MaxFrameBytes, frame.Length);
        Assert.True(PcmAudio.TryParse(frame, out var parsed, out var error), error);
        Assert.True(parsed.Input);
        Assert.Equal(7u, parsed.Sequence);
        Assert.Equal(1234ul, parsed.TimestampMs);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public void Frame_rejects_wrong_magic_and_version()
    {
        var frame = PcmAudio.BuildFrame(1, 0, true, PcmAudio.Silence());
        frame[0] = (byte)'X';
        Assert.False(PcmAudio.TryParse(frame, out _, out _));

        var versioned = PcmAudio.BuildFrame(1, 0, true, PcmAudio.Silence());
        versioned[2] = 9;
        Assert.False(PcmAudio.TryParse(versioned, out _, out var error));
        Assert.Contains("version", error);
    }

    [Fact]
    public void Frame_rejects_payload_larger_than_one_frame()
    {
        var frame = PcmAudio.BuildFrame(1, 0, true, new byte[PcmAudio.PayloadBytes]);
        var oversized = new byte[frame.Length + 2];
        frame.CopyTo(oversized, 0);
        Assert.False(PcmAudio.TryParse(oversized, out _, out _));
    }

    [Fact]
    public void Silence_is_exactly_one_20ms_frame()
    {
        Assert.Equal(1920, PcmAudio.Silence().Length);
        Assert.Equal(1920, PcmAudio.SampleRate * PcmAudio.FrameMs / 1000 * 2);
    }
}

public sealed class DiffieHellmanTests
{
    // A small prime keeps the arithmetic assertions fast; the shape is identical.
    private static readonly byte[] Prime = Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AACAA68FFFFFFFFFFFFFFFF");

    [Fact]
    public void Both_sides_derive_the_same_secret()
    {
        var dh = new BigIntegerDiffieHellman();
        var alice = dh.RandomSecret(256);
        var bob = dh.RandomSecret(256);

        var alicePublic = dh.PublicValue(Prime, 2, alice);
        var bobPublic = dh.PublicValue(Prime, 2, bob);

        var aliceShared = dh.SharedSecret(Prime, bobPublic, alice);
        var bobShared = dh.SharedSecret(Prime, alicePublic, bob);

        Assert.Equal(aliceShared, bobShared);
        Assert.Equal(Prime.Length, aliceShared.Length);
    }

    [Fact]
    public void Public_value_is_fixed_width_big_endian()
    {
        var dh = new BigIntegerDiffieHellman();
        var secret = dh.RandomSecret(256);
        var value = dh.PublicValue(Prime, 2, secret);
        Assert.Equal(Prime.Length, value.Length);
    }

    [Fact]
    public void Fingerprint_matches_sha1_tail_as_signed_little_endian()
    {
        var dh = new BigIntegerDiffieHellman();
        var key = RandomNumberGenerator.GetBytes(256);
        var hash = SHA1.HashData(key);
        var expected = BitConverter.ToInt64(hash, hash.Length - 8);
        Assert.Equal(expected, dh.Fingerprint(key));
    }

    [Fact]
    public void Peer_public_value_outside_range_is_rejected()
    {
        var dh = new BigIntegerDiffieHellman();
        var secret = dh.RandomSecret(256);
        var zero = new byte[Prime.Length];
        Assert.Throws<DhValidationException>(() => dh.SharedSecret(Prime, zero, secret));
    }
}

public sealed class MessageCompositionTests
{
    [Fact]
    public async Task Text_only_composition_has_no_entities()
    {
        var composition = await Compose([MessageSegment.Text("hello")]);
        Assert.Equal("hello", composition.Text);
        Assert.Empty(composition.Entities);
        Assert.Null(composition.ReplyToMessageId);
    }

    [Fact]
    public async Task Reply_segment_becomes_reply_to_message_id()
    {
        var composition = await Compose([MessageSegment.Reply(42), MessageSegment.Text("hi")]);
        Assert.Equal(42, composition.ReplyToMessageId);
        Assert.Equal("hi", composition.Text);
    }

    [Fact]
    public async Task Duplicate_reply_segments_are_rejected()
    {
        var error = await Assert.ThrowsAsync<MilkyActionException>(() =>
            Compose([MessageSegment.Reply(1), MessageSegment.Reply(2), MessageSegment.Text("x")]));
        Assert.Equal(MilkyErrorCode.BadRequest, error.Code);
    }

    [Fact]
    public async Task Unsupported_segment_type_is_rejected_not_dropped()
    {
        var segment = new MessageSegment
        {
            Type = "image",
            Data = System.Text.Json.JsonSerializer.SerializeToElement(new { file_id = "x" }),
        };
        var error = await Assert.ThrowsAsync<MilkyActionException>(() => Compose([segment]));
        Assert.Equal(MilkyErrorCode.Unsupported, error.Code);
        Assert.Contains("image", error.Message);
    }

    [Fact]
    public async Task Empty_text_is_rejected()
    {
        var error = await Assert.ThrowsAsync<MilkyActionException>(() =>
            Compose([MessageSegment.Text("")]));
        Assert.Equal(MilkyErrorCode.BadRequest, error.Code);
    }

    private static Task<OutgoingComposition> Compose(IReadOnlyList<MessageSegment> segments)
        => TestMapper.Create().ComposeAsync(
            new SessionContext(TestMapper.Account, 1, "token"), segments, CancellationToken.None);
}
