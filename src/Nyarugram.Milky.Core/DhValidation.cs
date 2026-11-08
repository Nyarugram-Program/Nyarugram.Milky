using System.Numerics;
using System.Security.Cryptography;

namespace Nyarugram.Milky.Core;

/// <summary>
/// A peer supplied DH material that is structurally invalid. This is a permanent, local
/// verdict: retrying the same call cannot succeed, so the call must fail in a controlled way
/// instead of throwing into the sync loop.
/// </summary>
public sealed class DhValidationException : Exception
{
    public DhValidationException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

/// <summary>
/// Validation of the call key agreement inputs. Length checks alone are insufficient: the
/// public value must also be inside the safe multiplicative range for the agreed prime.
/// </summary>
public static class DhValidation
{
    public const int PrimeLength = 256;
    public const int HashLength = 32;

    /// <summary>
    /// The backend pins a 2048-bit prime with generator 3. A peer cannot change p or g, but
    /// the values are still checked so a malformed config never reaches modular arithmetic.
    /// </summary>
    public static void ValidatePrime(byte[] prime, int generator)
    {
        if (prime is not { Length: PrimeLength })
            throw new DhValidationException("CALL_INVALID_PRIME", "DH prime must be 2048 bits");
        if (generator is < 2 or > 5)
            throw new DhValidationException("CALL_INVALID_GENERATOR", "DH generator is outside the accepted range");
        if ((prime[0] & 0x80) == 0)
            throw new DhValidationException("CALL_INVALID_PRIME", "DH prime does not have a 2048-bit modulus");
        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        if (p.IsEven || p <= 3)
            throw new DhValidationException("CALL_INVALID_PRIME", "DH prime is not a valid odd modulus");
        if (generator >= p)
            throw new DhValidationException("CALL_INVALID_GENERATOR", "DH generator is not smaller than the prime");
    }

    /// <summary>
    /// Rejects 0, 1, p-1, p, any value of the wrong width and anything outside (1, p-1).
    /// Those values break the shared-secret derivation or collapse it to a known constant.
    /// </summary>
    public static void ValidatePublicValue(byte[]? value, byte[] prime, string field)
    {
        if (value is null || value.Length != prime.Length)
            throw new DhValidationException($"{field}_INVALID", $"{field} must be {prime.Length} bytes");
        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        var v = new BigInteger(value, isUnsigned: true, isBigEndian: true);
        if (v <= BigInteger.One || v >= p - BigInteger.One)
            throw new DhValidationException($"{field}_OUT_OF_RANGE", $"{field} is outside the safe range");
    }

    public static void ValidateHash(byte[]? hash, string field)
    {
        if (hash is not { Length: HashLength })
            throw new DhValidationException($"{field}_INVALID", $"{field} must be a SHA-256 digest");
    }

    /// <summary>Constant-time comparison of SHA-256(g_a) with the caller's commitment.</summary>
    public static bool MatchesCommitment(byte[] publicValue, byte[] commitment)
        => commitment.Length == HashLength &&
           CryptographicOperations.FixedTimeEquals(SHA256.HashData(publicValue), commitment);
}
