using System.Numerics;
using System.Security.Cryptography;
using Nyarugram.Milky.Core;

namespace Nyarugram.Milky.Media;

/// <summary>
/// Diffie-Hellman over the backend-supplied prime, using big-endian unsigned encoding
/// exactly as the wire format requires. Randomness comes from the OS CSPRNG.
/// </summary>
public sealed class BigIntegerDiffieHellman : IDiffieHellman
{
    public byte[] RandomSecret(int bytes)
    {
        if (bytes < 32) throw new ArgumentOutOfRangeException(nameof(bytes));
        var buffer = new byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

    public byte[] PublicValue(byte[] prime, int generator, byte[] secret)
    {
        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        var exponent = new BigInteger(secret, isUnsigned: true, isBigEndian: true);
        var value = BigInteger.ModPow(generator, exponent, p);
        return ToFixed(value, prime.Length);
    }

    public byte[] SharedSecret(byte[] prime, byte[] otherPublic, byte[] secret)
    {
        // Defence in depth: callers validate first, but the arithmetic layer never accepts a
        // degenerate public value even if a new caller forgets.
        DhValidation.ValidatePublicValue(otherPublic, prime, "CALL_PEER_PUBLIC");
        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        var other = new BigInteger(otherPublic, isUnsigned: true, isBigEndian: true);
        var exponent = new BigInteger(secret, isUnsigned: true, isBigEndian: true);
        var value = BigInteger.ModPow(other, exponent, p);
        return ToFixed(value, prime.Length);
    }

    public byte[] Sha256(byte[] value) => SHA256.HashData(value);

    /// <summary>
    /// Telegram call fingerprint: the last 8 bytes of SHA-1 read as a little-endian
    /// signed 64-bit integer. Clients compare this exact value.
    /// </summary>
    public long Fingerprint(byte[] key)
    {
        var hash = SHA1.HashData(key);
        return BitConverter.ToInt64(hash, hash.Length - 8);
    }

    private static byte[] ToFixed(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == length) return bytes;
        if (bytes.Length > length) throw new InvalidOperationException("DH value wider than the prime");
        var padded = new byte[length];
        bytes.CopyTo(padded, length - bytes.Length);
        return padded;
    }
}
