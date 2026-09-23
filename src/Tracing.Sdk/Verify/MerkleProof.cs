using Tracing.Sdk.Hash;

namespace Tracing.Sdk.Verify;

/// <summary>
/// Merkle inclusion proofs over Keccak-256 with sorted pairs — the layout of
/// OpenZeppelin's <c>MerkleProof.verify</c> (and merkletreejs with
/// <c>sortPairs</c>). The leaf is the record hash itself, and each parent is
/// keccak256 of its two children in ascending byte order, so a proof is just
/// the sibling hashes from the leaf up to the root, with no left/right flags.
/// </summary>
public static class MerkleProof
{
    private static readonly Keccak256Hasher Hasher = new();

    /// <summary>Fold a leaf and its sibling path into the tree root.</summary>
    /// <param name="leaf">the record hash</param>
    /// <param name="siblings">sibling hashes, ordered from the leaf up to the root;
    /// empty for a single-leaf tree, whose root is the leaf itself</param>
    /// <returns>the 32-byte root</returns>
    public static byte[] ComputeRoot(byte[] leaf, IEnumerable<byte[]> siblings)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ArgumentNullException.ThrowIfNull(siblings);

        var node = leaf;

        foreach (var sibling in siblings)
        {
            node = HashPair(node, sibling);
        }

        return node;
    }

    /// <summary>keccak256 of the two nodes concatenated in ascending byte order.</summary>
    public static byte[] HashPair(byte[] a, byte[] b)
    {
        var (first, second) = a.AsSpan().SequenceCompareTo(b) <= 0 ? (a, b) : (b, a);

        return Hasher.Hash([.. first, .. second]);
    }
}
