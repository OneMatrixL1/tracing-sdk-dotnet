using System.Globalization;
using System.Text.Json;

namespace Tracing.Sdk.Events;

/// <summary>
/// The event types an SDK instance can anchor, by name. Built in code with
/// <see cref="Register(EventTypeDefinition)"/>, or loaded from a JSON file in
/// the shape of the backend's <c>plugin.yaml</c>:
/// </summary>
/// <example>
/// <code>
/// {
///   "plugin": "tickets",
///   "eventTypes": [
///     {
///       "name": "batch",
///       "anchoring": { "mode": "merkle", "merkle": { "params": { "maxTreeSize": 100000 } } },
///       "request": { "fields": [ { "name": "draw", "type": "string", "required": true }, ... ] },
///       "hashing": { "dataType": "json", "leafPrefix": "0x00" },
///       "link": { "fields": ["draw", "channel", "sequence"], "leafParam": "ticketHash" }
///     }
///   ]
/// }
/// </code>
/// <c>hashing</c>, <c>link</c> and the field role <c>sequence</c> are SDK
/// additions. Members the SDK does not use (<c>response</c>, <c>chain</c>,
/// <c>merkle.batching</c>, ...) are ignored, so a plugin file converts to JSON
/// as it is.
/// </example>
public sealed class EventTypeRegistry
{
    private readonly Dictionary<string, EventTypeDefinition> _types = new(StringComparer.Ordinal);

    /// <summary>A registry holding only the default <c>anchor</c> type.</summary>
    public static EventTypeRegistry Default() => new EventTypeRegistry().Register(EventPresets.Anchor);

    /// <summary>The registered names, in no particular order.</summary>
    public IReadOnlyCollection<string> Names => _types.Keys;

    /// <summary>Validate and add one event type.</summary>
    /// <returns>this registry, for chaining</returns>
    /// <exception cref="ConfigException">the definition is inconsistent, or its name is taken</exception>
    public EventTypeRegistry Register(EventTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();

        if (!_types.TryAdd(definition.Name, definition))
        {
            throw new ConfigException($"Event type \"{definition.Name}\" is registered twice");
        }

        return this;
    }

    /// <inheritdoc cref="Register(EventTypeDefinition)"/>
    public EventTypeRegistry Register(IEnumerable<EventTypeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var definition in definitions)
        {
            Register(definition);
        }

        return this;
    }

    public bool TryGet(string name, out EventTypeDefinition definition) => _types.TryGetValue(name, out definition!);

    /// <exception cref="ConfigException">no event type has this name</exception>
    public EventTypeDefinition Get(string name) =>
        _types.TryGetValue(name ?? "", out var definition)
            ? definition
            : throw new ConfigException($"Unknown event type \"{name}\"; registered: {string.Join(", ", _types.Keys.Order(StringComparer.Ordinal))}");

    /// <summary>A registry holding the event types of a JSON document.</summary>
    /// <exception cref="ConfigException">the JSON is malformed or a definition is inconsistent</exception>
    public static EventTypeRegistry FromJson(string json) => new EventTypeRegistry().Register(ParseJson(json));

    /// <inheritdoc cref="FromJson(string)"/>
    public static EventTypeRegistry FromJsonFile(string path)
    {
        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (IOException e)
        {
            throw new ConfigException($"Event type file cannot be read: {path}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Parse the event types of a JSON document: an object with an
    /// <c>eventTypes</c> array, or the array itself. Not yet validated.
    /// </summary>
    /// <exception cref="ConfigException">the JSON is malformed</exception>
    public static IReadOnlyList<EventTypeDefinition> ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            return ParseEventTypes(document.RootElement);
        }
        catch (JsonException e)
        {
            throw new ConfigException("Event types are not valid JSON: " + e.Message, e);
        }
    }

    internal static IReadOnlyList<EventTypeDefinition> ParseEventTypes(JsonElement root)
    {
        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("eventTypes", out var eventTypes) ? eventTypes : root;

        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigException("Event types must be an array, or an object with an \"eventTypes\" array");
        }

        return list.EnumerateArray().Select(ParseEventType).ToList();
    }

    private static EventTypeDefinition ParseEventType(JsonElement element)
    {
        RequireObject(element, "an event type");
        var name = String(element, "name") ?? throw new ConfigException("An event type needs a \"name\"");
        var where = $"event type \"{name}\"";

        var anchoring = new AnchoringDefinition();

        if (Member(element, "anchoring") is { } a)
        {
            RequireObject(a, where + " anchoring");
            var maxTreeSize = Member(a, "merkle") is { } merkle && Member(merkle, "params") is { } p && Member(p, "maxTreeSize") is { } max
                ? max.TryGetInt32(out var m) ? m : throw new ConfigException($"{where}: maxTreeSize must be an integer")
                : (int?)null;

            anchoring = new AnchoringDefinition(
                Enum(String(a, "mode") ?? "transactionHash", where + " anchoring.mode",
                    ("transactionHash", AnchoringMode.TransactionHash), ("merkle", AnchoringMode.Merkle)),
                Enum(String(a, "key") ?? "client", where + " anchoring.key",
                    ("client", KeyMode.Client), ("derived", KeyMode.Derived)),
                maxTreeSize);
        }

        var fields = Member(element, "request") is { } request && Member(request, "fields") is { ValueKind: JsonValueKind.Array } f
            ? f.EnumerateArray().Select(field => ParseField(field, where)).ToList()
            : throw new ConfigException($"{where} needs a \"request.fields\" array");

        HashingDefinition? hashing = null;

        if (Member(element, "hashing") is { } h)
        {
            RequireObject(h, where + " hashing");
            hashing = new HashingDefinition(
                String(h, "dataType") is { } dataType
                    ? Enum(dataType, where + " hashing.dataType", ("json", DataType.Json), ("xml", DataType.Xml), ("raw", DataType.Raw))
                    : null,
                String(h, "leafPrefix") is { } prefix ? ParseByte(prefix, where + " hashing.leafPrefix") : null);
        }

        LinkDefinition? link = null;

        if (Member(element, "link") is { } l)
        {
            RequireObject(l, where + " link");
            var defaults = new LinkDefinition();
            link = new LinkDefinition(
                Member(l, "fields") is { ValueKind: JsonValueKind.Array } names
                    ? names.EnumerateArray().Select(n => n.ValueKind == JsonValueKind.String
                        ? n.GetString()!
                        : throw new ConfigException($"{where}: link.fields must hold strings")).ToList()
                    : null,
                String(l, "leafParam") ?? defaults.LeafParam,
                String(l, "keyParam") ?? defaults.KeyParam);
        }

        return new EventTypeDefinition
        {
            Name = name,
            Anchoring = anchoring,
            RequestFields = fields,
            Hashing = hashing,
            Link = link,
        };
    }

    private static FieldDefinition ParseField(JsonElement element, string where)
    {
        RequireObject(element, where + " field");
        var name = String(element, "name") ?? throw new ConfigException($"{where}: every field needs a \"name\"");
        where = $"{where} field \"{name}\"";

        var type = Enum(String(element, "type") ?? throw new ConfigException($"{where} needs a \"type\""), where + " type",
            ("string", FieldType.String), ("bytes32", FieldType.Bytes32), ("bytes", FieldType.Bytes),
            ("uint64", FieldType.Uint64), ("bytes32[]", FieldType.Bytes32Array));
        var role = String(element, "role") is { } r
            ? Enum(r, where + " role", ("key", FieldRole.Key), ("leaves", FieldRole.Leaves), ("sequence", FieldRole.Sequence))
            : FieldRole.None;
        var required = Member(element, "required") is { } req
            ? req.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ConfigException($"{where}: required must be true or false"),
            }
            : false;

        return new FieldDefinition(name, type, required, role);
    }

    private static T Enum<T>(string value, string where, params (string Name, T Value)[] choices) =>
        choices.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.OrdinalIgnoreCase)) is { Name: not null } match
            ? match.Value
            : throw new ConfigException($"{where}: unknown value \"{value}\", expected one of {string.Join(", ", choices.Select(c => c.Name))}");

    private static byte ParseByte(string value, string where)
    {
        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

        return digits.Length is 1 or 2 && byte.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b)
            ? b
            : throw new ConfigException($"{where}: expected one hex byte such as \"0x00\", got \"{value}\"");
    }

    private static JsonElement? Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? String(JsonElement element, string name) => Member(element, name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        _ => throw new ConfigException($"\"{name}\" must be a string"),
    };

    private static void RequireObject(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"{what} must be a JSON object");
        }
    }
}
