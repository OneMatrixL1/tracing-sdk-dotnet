using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tracing.Sdk.Hash;

namespace Tracing.Sdk.Verify;

/// <summary>A decoded <c>Anchored(bytes32,uint64)</c> event.</summary>
/// <param name="DataHash">the anchored record hash, 0x-prefixed lowercase hex</param>
/// <param name="SigningTime">the event's uint64 argument</param>
public readonly record struct AnchoredEvent(string DataHash, ulong SigningTime);

/// <summary>
/// ABI-decodes the Solidity event the anchoring contract emits for every
/// anchored record, plus the small hex helpers that come with reading receipt
/// logs.
/// </summary>
public sealed class AnchoredEventDecoder(Keccak256Hasher hasher)
{
    /// <summary>Solidity signature the event's topics[0] is derived from.</summary>
    public const string EventSignature = "Anchored(bytes32,uint64)";

    private string? _topic;

    /// <summary>
    /// keccak256 of the event signature — the topics[0] every anchor log
    /// carries, lowercase and 0x-prefixed.
    /// </summary>
    public string Topic() => _topic ??= Keccak256Hasher.ToHex(hasher.Hash(Encoding.UTF8.GetBytes(EventSignature)));

    /// <summary>
    /// Decode one receipt log as Anchored(bytes32,uint64).
    /// </summary>
    /// <remarks>
    /// Returns null when the log is not an anchor event at all — wrong
    /// topics[0], or too few words to hold both arguments. Indexed and
    /// non-indexed arguments are both accepted: the ABI puts indexed values in
    /// topics[1..] in declaration order and the rest in data, so reading
    /// topics-then-data recovers the argument list either way.
    /// </remarks>
    /// <param name="log">a log object from an eth_getTransactionReceipt result</param>
    public AnchoredEvent? Decode(JsonElement log)
    {
        if (log.ValueKind != JsonValueKind.Object
            || !log.TryGetProperty("topics", out var topicsElement)
            || topicsElement.ValueKind != JsonValueKind.Array
            || topicsElement.GetArrayLength() == 0)
        {
            return null;
        }

        var topics = topicsElement.EnumerateArray().Select(AsString).ToList();

        if (HexBody(topics[0]) != HexBody(Topic()))
        {
            return null;
        }

        // The event's argument words, in declaration order.
        var words = topics.Skip(1).Select(HexBody).ToList();
        var data = HexBody(log.TryGetProperty("data", out var dataElement) ? AsString(dataElement) : "");

        for (var i = 0; i < data.Length; i += 64)
        {
            words.Add(data.Substring(i, Math.Min(64, data.Length - i)));
        }

        if (words.Count < 2 || words[0].Length != 64 || words[1].Length != 64
            || !IsHex(words[0]) || !IsHex(words[1]))
        {
            return null;
        }

        // uint64 is right-aligned in its 32-byte word: the low 8 bytes hold it.
        var signingTime = ulong.Parse(words[1][48..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        return new AnchoredEvent("0x" + words[0], signingTime);
    }

    /// <summary>
    /// Validate a 32-byte hex hash and return it lowercased and 0x-prefixed,
    /// so comparing two hashes is plain string equality.
    /// </summary>
    /// <exception cref="ConfigException">the value is empty or not 32 bytes of hex</exception>
    public static string NormalizeHash(string? hash, string label)
    {
        var body = HexBody(hash ?? "");

        if (body.Length == 0)
        {
            throw new ConfigException($"{label} is required");
        }

        if (body.Length != 64 || !IsHex(body))
        {
            throw new ConfigException($"{label} must be a 32-byte hex string, got \"{hash}\"");
        }

        return "0x" + body;
    }

    /// <summary>Strip an optional 0x prefix and lowercase the remaining hex digits.</summary>
    public static string HexBody(string hex)
    {
        var lower = hex.Trim().ToLowerInvariant();

        return lower.StartsWith("0x", StringComparison.Ordinal) ? lower[2..] : lower;
    }

    private static bool IsHex(string value) => Regex.IsMatch(value, "^[0-9a-f]*$");

    private static string AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : element.ToString();
}
