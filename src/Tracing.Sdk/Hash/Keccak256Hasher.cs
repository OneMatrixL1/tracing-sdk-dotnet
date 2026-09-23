using Org.BouncyCastle.Crypto.Digests;

namespace Tracing.Sdk.Hash;

/// <summary>
/// Keccak-256 (the original NIST submission, not FIPS-202 SHA3-256 — this is
/// the variant used by Ethereum/EVM-style integrity proofs). .NET's own
/// <c>SHA3_256</c> is the FIPS variant and produces different hashes, so this
/// delegates to BouncyCastle's <c>KeccakDigest</c>.
/// </summary>
public sealed class Keccak256Hasher
{
    /// <returns>the 32-byte digest</returns>
    public byte[] Hash(byte[] canonical)
    {
        var digest = new KeccakDigest(256);
        digest.BlockUpdate(canonical, 0, canonical.Length);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);

        return output;
    }

    /// <summary>A digest as the SDK writes hashes: 0x-prefixed lowercase hex.</summary>
    public static string ToHex(byte[] digest) => "0x" + Convert.ToHexStringLower(digest);
}
