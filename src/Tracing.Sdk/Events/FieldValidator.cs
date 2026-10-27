using System.Collections;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tracing.Sdk.Hash;

namespace Tracing.Sdk.Events;

/// <summary>
/// Turns caller values into an event type's request body, applying the
/// backend's rules so a bad request fails before any HTTP call: undeclared
/// fields are refused, bytes32 is 32 non-zero bytes, uint64 stays exact above
/// 2^53, leaves are 1 to maxTreeSize distinct bytes32.
/// </summary>
internal static partial class FieldValidator
{
    /// <summary>The whole request body of one record.</summary>
    /// <exception cref="ConfigException">a field is unknown, missing or invalid</exception>
    public static JsonObject BuildRequest(EventTypeDefinition definition, IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var where = $"Event type \"{definition.Name}\"";

        foreach (var name in fields.Keys)
        {
            if (!definition.RequestFields.Any(f => f.Name == name))
            {
                throw new ConfigException($"{where}: unknown field \"{name}\"; declared: {string.Join(", ", definition.RequestFields.Select(f => f.Name))}");
            }
        }

        var body = new JsonObject();

        foreach (var field in definition.RequestFields)
        {
            if (!fields.TryGetValue(field.Name, out var value) || value is null)
            {
                if (field.Required)
                {
                    throw new ConfigException($"{where}: missing required field \"{field.Name}\"");
                }

                continue;
            }

            body[field.Name] = Normalize(definition, field, value, where);
        }

        return body;
    }

    /// <summary>One value of a field, normalized as it is sent.</summary>
    /// <exception cref="ConfigException">the value does not fit the field's type</exception>
    public static JsonNode Normalize(EventTypeDefinition definition, FieldDefinition field, object value, string where)
    {
        var label = $"{where}: field \"{field.Name}\"";

        switch (field.Type)
        {
            case FieldType.String:
                var text = AsString(value) ?? throw Invalid(label, "a string", value);

                if (field.Required && text.Length == 0)
                {
                    throw new ConfigException($"{label} must not be empty");
                }

                return JsonValue.Create(text);

            case FieldType.Bytes32:
                return JsonValue.Create(Bytes32(value, label));

            case FieldType.Bytes:
                return JsonValue.Create(Bytes(value, label));

            case FieldType.Uint64:
                return JsonValue.Create(Uint64(value, label));

            case FieldType.Bytes32Array:
                return Leaves(definition, value, $"{where}: field \"{field.Name}", label);

            default:
                throw new ConfigException($"{label} has an unsupported type {field.Type}");
        }
    }

    /// <summary>A bytes32 as lowercase 0x hex.</summary>
    /// <exception cref="ConfigException">not 32 bytes of hex, or all zeros</exception>
    public static string Bytes32(object value, string label)
    {
        string hex;

        if (value is byte[] bytes)
        {
            hex = bytes.Length == 32 ? Keccak256Hasher.ToHex(bytes) : throw new ConfigException($"{label} must be 32 bytes, got {bytes.Length}");
        }
        else
        {
            var text = AsString(value) ?? throw Invalid(label, "a 32-byte hex string", value);
            var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;

            if (digits.Length != 64 || !HexDigits().IsMatch(digits))
            {
                throw new ConfigException($"{label} must be 64 hex digits, got \"{text}\"");
            }

            hex = "0x" + digits.ToLowerInvariant();
        }

        if (hex.AsSpan(2).TrimStart('0').IsEmpty)
        {
            throw new ConfigException($"{label} must not be all zeros");
        }

        return hex;
    }

    private static string Bytes(object value, string label)
    {
        if (value is byte[] bytes)
        {
            return Keccak256Hasher.ToHex(bytes);
        }

        var text = AsString(value) ?? throw Invalid(label, "0x-prefixed hex or a byte[]", value);

        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.Length % 2 != 0 || !HexDigits().IsMatch(text.AsSpan(2)))
        {
            throw new ConfigException($"{label} must be 0x-prefixed hex with an even number of digits");
        }

        return "0x" + text[2..].ToLowerInvariant();
    }

    private static ulong Uint64(object value, string label)
    {
        if (value is JsonNode node)
        {
            return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<ulong>(out var u)
                ? u
                : throw Invalid(label, "an integer from 0 to 2^64-1", value);
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var u)
                ? u
                : throw Invalid(label, "an integer from 0 to 2^64-1", value);
        }

        BigInteger? number = value switch
        {
            ulong n => n,
            uint n => n,
            ushort n => n,
            byte n => n,
            long n => n,
            int n => n,
            short n => n,
            sbyte n => n,
            BigInteger n => n,
            _ => null,
        };

        return number is { } big && big >= 0 && big <= ulong.MaxValue
            ? (ulong)big
            : throw Invalid(label, "an integer from 0 to 2^64-1", value);
    }

    // itemPrefix: the label of one leaf, without its closing [index]".
    private static JsonArray Leaves(EventTypeDefinition definition, object value, string itemPrefix, string label)
    {
        IEnumerable items = value switch
        {
            JsonArray array => array,
            JsonElement { ValueKind: JsonValueKind.Array } element => element.EnumerateArray().Select(e => (object)e).ToList(),
            string or byte[] => throw Invalid(label, "a list of bytes32", value),
            IEnumerable enumerable => enumerable,
            _ => throw Invalid(label, "a list of bytes32", value),
        };

        var leaves = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var index = leaves.Count;
            var itemLabel = $"{itemPrefix}[{index}]\"";
            var leaf = Bytes32(item ?? throw new ConfigException($"{itemLabel} is null"), itemLabel);

            if (seen.TryGetValue(leaf, out var first))
            {
                throw new ConfigException($"{itemLabel} repeats index {first} ({leaf})");
            }

            seen[leaf] = index;
            leaves.Add(leaf);
        }

        if (leaves.Count == 0)
        {
            throw new ConfigException($"{label} must hold at least one leaf");
        }

        if (definition.Anchoring.MaxTreeSize is { } max && leaves.Count > max)
        {
            throw new ConfigException($"{label} holds {leaves.Count} leaves, at most {max} are allowed");
        }

        return new JsonArray(leaves.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
    }

    private static string? AsString(object value) => value switch
    {
        string s => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    private static ConfigException Invalid(string label, string expected, object value) =>
        new($"{label} must be {expected}, got {Describe(value)}");

    private static string Describe(object value) => value switch
    {
        JsonNode node => node.ToJsonString(),
        JsonElement element => element.GetRawText(),
        string s => $"\"{s}\"",
        _ => $"{value} ({value.GetType().Name})",
    };

    [GeneratedRegex("^[0-9a-fA-F]*$")]
    private static partial Regex HexDigits();
}
