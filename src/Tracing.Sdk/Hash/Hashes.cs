using System.Security.Cryptography;
using Tracing.Sdk.Canonicalize;
using Tracing.Sdk.Events;

namespace Tracing.Sdk.Hash;

/// <summary>
/// Builds the hashes a client sends. The backend never hashes a record: every
/// bytes32 in a request (a record hash, a ticket hash) is the client's, and
/// whoever checks the record later must hash it the same way. Every helper
/// returns the raw digest; <see cref="Keccak256Hasher.ToHex"/> gives its 0x form.
/// </summary>
public static class Hashes
{
    /// <summary>The leaf prefix the tickets guide recommends: a byte no inner node (0x01) or root (0x02) uses.</summary>
    public const byte DefaultLeafPrefix = 0x00;

    private static readonly Keccak256Hasher Hasher = new();

    private static readonly Dictionary<DataType, ICanonicalizer> Canonicalizers = new()
    {
        [DataType.Json] = new JsonCanonicalizer(),
        [DataType.Xml] = new XmlCanonicalizer(),
        [DataType.Raw] = new RawCanonicalizer(),
    };

    /// <summary>
    /// keccak256 of the record's canonical bytes: the hash of a direct record
    /// (<c>events</c>, <c>recon</c>, <c>subagent</c>, the default <c>anchor</c>).
    /// </summary>
    /// <param name="rawData">the record; a string is hashed as its UTF-8 bytes</param>
    /// <param name="dataType">how the record is canonicalized first</param>
    /// <exception cref="CanonicalizationException">rawData cannot be canonicalized</exception>
    public static byte[] Record(string rawData, DataType dataType) => Record(TracingSdk.EncodeRawData(rawData), dataType);

    /// <inheritdoc cref="Record(string, DataType)"/>
    public static byte[] Record(byte[] rawData, DataType dataType) => Hasher.Hash(Canonicalize(rawData, dataType));

    /// <summary>
    /// keccak256(prefix || canonical bytes): the hash of one leaf of a Merkle
    /// type, e.g. a ticket hash for <c>batch.ticketHashes</c>.
    /// </summary>
    /// <param name="rawData">the ticket; a string is hashed as its UTF-8 bytes</param>
    /// <param name="dataType">how the ticket is canonicalized first</param>
    /// <param name="prefix">the byte put first; 0x01 and 0x02 are refused, so a leaf
    /// can never be mistaken for an inner node or a batch root</param>
    /// <exception cref="ConfigException">prefix is 0x01 or 0x02</exception>
    /// <exception cref="CanonicalizationException">rawData cannot be canonicalized</exception>
    public static byte[] Leaf(string rawData, DataType dataType, byte prefix = DefaultLeafPrefix) =>
        Leaf(TracingSdk.EncodeRawData(rawData), dataType, prefix);

    /// <inheritdoc cref="Leaf(string, DataType, byte)"/>
    public static byte[] Leaf(byte[] rawData, DataType dataType, byte prefix = DefaultLeafPrefix)
    {
        if (prefix is 0x01 or 0x02)
        {
            throw new ConfigException($"Leaf prefix 0x{prefix:x2} is reserved for inner nodes and roots");
        }

        return Hasher.Hash([prefix, .. Canonicalize(rawData, dataType)]);
    }

    /// <summary>The leaves of one batch, in the order given; that order is the tree's.</summary>
    /// <inheritdoc cref="Leaf(string, DataType, byte)"/>
    public static IReadOnlyList<byte[]> Leaves(IEnumerable<string> rawData, DataType dataType, byte prefix = DefaultLeafPrefix) =>
        rawData.Select(r => Leaf(r, dataType, prefix)).ToList();

    /// <inheritdoc cref="Leaves(IEnumerable{string}, DataType, byte)"/>
    public static IReadOnlyList<byte[]> Leaves(IEnumerable<byte[]> rawData, DataType dataType, byte prefix = DefaultLeafPrefix) =>
        rawData.Select(r => Leaf(r, dataType, prefix)).ToList();

    /// <summary>
    /// keccak256(utf8(draw)): the <c>drawHash</c> topic of the <c>events</c> and
    /// <c>recon</c> logs, which lists every record of one draw in an index.
    /// </summary>
    public static byte[] DrawHash(string draw) => Hasher.Hash(TracingSdk.EncodeRawData(draw));

    /// <summary>
    /// SHA-256 over the 32 raw bytes of a drawRoot (not its hex text): the
    /// message imprint the timestamp authority must sign for <c>lockts</c>.
    /// </summary>
    /// <param name="drawRoot">the <c>key</c> a <c>lock</c> answered, 0x hex</param>
    /// <exception cref="ConfigException">drawRoot is not 32 non-zero bytes of hex</exception>
    public static byte[] LockTimestampImprint(string drawRoot) =>
        SHA256.HashData(Convert.FromHexString(FieldValidator.Bytes32(drawRoot ?? throw new ConfigException("drawRoot is required"), "drawRoot")[2..]));

    /// <inheritdoc cref="LockTimestampImprint(string)"/>
    public static byte[] LockTimestampImprint(byte[] drawRoot)
    {
        ArgumentNullException.ThrowIfNull(drawRoot);

        return drawRoot.Length == 32
            ? SHA256.HashData(drawRoot)
            : throw new ConfigException($"drawRoot must be 32 bytes, got {drawRoot.Length}");
    }

    private static byte[] Canonicalize(byte[] rawData, DataType dataType)
    {
        ArgumentNullException.ThrowIfNull(rawData);

        return Canonicalizers.TryGetValue(dataType, out var canonicalizer)
            ? canonicalizer.Canonicalize(rawData)
            : throw new ConfigException($"Unsupported dataType {dataType}");
    }
}
