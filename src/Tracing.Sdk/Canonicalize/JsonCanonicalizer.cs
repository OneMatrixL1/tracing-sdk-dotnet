using System.Text;
using System.Text.Json;
using Org.Webpki.Es6NumberSerialization;

namespace Tracing.Sdk.Canonicalize;

/// <summary>
/// JSON Canonicalization Scheme per RFC 8785 (JCS): decode, then re-serialize
/// with object member names sorted by UTF-16 code unit value, numbers
/// formatted per the ECMAScript Number::toString algorithm (so <c>1</c>,
/// <c>1.0</c>, and <c>1e0</c> all collapse to <c>1</c>), and minimal string
/// escaping.
/// </summary>
/// <remarks>
/// Number formatting is the vendored ES6 serializer by RFC 8785 co-author
/// Anders Rundgren (Vendor/Es6NumberSerializer); the string escaping follows
/// his reference JsonCanonicalizer. Parsing is System.Text.Json, configured to
/// accept and reject the same documents as the PHP SDK's json_decode: at most
/// 511 nested containers, the last of duplicate member names wins, and a byte
/// order mark, invalid UTF-8, or a lone surrogate is an error.
/// </remarks>
public sealed class JsonCanonicalizer : ICanonicalizer
{
    private const int MaxDepth = 511;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public byte[] Canonicalize(byte[] rawData)
    {
        try
        {
            using var document = JsonDocument.Parse(rawData, new JsonDocumentOptions { MaxDepth = MaxDepth });
            var buffer = new StringBuilder();
            Serialize(document.RootElement, buffer);

            return StrictUtf8.GetBytes(buffer.ToString());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            // JsonException: malformed or too deep; InvalidOperationException:
            // invalid UTF-8 or a lone surrogate in a string; ArgumentException:
            // a number that overflowed to Infinity (e.g. 1e400).
            throw new CanonicalizationException("Invalid JSON payload: " + e.Message, e);
        }
    }

    private static void Serialize(JsonElement element, StringBuilder buffer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Last duplicate wins, as in json_decode and JSON.parse.
                var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

                foreach (var property in element.EnumerateObject())
                {
                    members[property.Name] = property.Value;
                }

                buffer.Append('{');
                var first = true;

                // Ordinal comparison is UTF-16 code unit order, as JCS requires.
                foreach (var name in members.Keys.Order(StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        buffer.Append(',');
                    }

                    first = false;
                    SerializeString(name, buffer);
                    buffer.Append(':');
                    Serialize(members[name], buffer);
                }

                buffer.Append('}');
                break;

            case JsonValueKind.Array:
                buffer.Append('[');
                var next = false;

                foreach (var item in element.EnumerateArray())
                {
                    if (next)
                    {
                        buffer.Append(',');
                    }

                    next = true;
                    Serialize(item, buffer);
                }

                buffer.Append(']');
                break;

            case JsonValueKind.String:
                SerializeString(element.GetString()!, buffer);
                break;

            case JsonValueKind.Number:
                buffer.Append(NumberToJson.SerializeNumber(element.GetDouble()));
                break;

            case JsonValueKind.True:
                buffer.Append("true");
                break;

            case JsonValueKind.False:
                buffer.Append("false");
                break;

            default:
                buffer.Append("null");
                break;
        }
    }

    /// <summary>
    /// RFC 8785 section 3.2.2.2: escape only the quote, the backslash, and
    /// control characters, using the short forms where JSON has them.
    /// </summary>
    private static void SerializeString(string value, StringBuilder buffer)
    {
        buffer.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\n': buffer.Append("\\n"); break;
                case '\b': buffer.Append("\\b"); break;
                case '\f': buffer.Append("\\f"); break;
                case '\r': buffer.Append("\\r"); break;
                case '\t': buffer.Append("\\t"); break;
                case '"': buffer.Append("\\\""); break;
                case '\\': buffer.Append("\\\\"); break;
                default:
                    if (c < ' ')
                    {
                        buffer.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        buffer.Append(c);
                    }

                    break;
            }
        }

        buffer.Append('"');
    }
}
