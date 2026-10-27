using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tracing.Sdk.Events;
using Tracing.Sdk.Hash;
using Tracing.Sdk.Transport;

namespace Tracing.Sdk;

/// <summary>
/// Sends records of the configured event types to the Indexer, hashes them
/// the way each type says, and builds the integrity-portal links that verify
/// them. Holds HTTP connections; dispose it when done, or keep one instance
/// for the application's lifetime.
/// </summary>
public sealed class TracingSdk : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly SendOptions _defaultOptions;
    private readonly Dictionary<string, EventTypeDefinition> _eventTypes;
    private readonly PortalConfig _portal;
    private IIndexerTransport _transport;

    /// <exception cref="ConfigException">missing or invalid config, or an mTLS file that cannot be loaded</exception>
    public TracingSdk(TracingSdkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrEmpty(config.Endpoint))
        {
            throw new ConfigException("Missing required config key \"endpoint\"");
        }

        ValidateAuth(config.Auth ?? throw new ConfigException("Missing required config key \"auth\""));

        _defaultOptions = config.Options ?? new SendOptions();
        var eventTypes = config.EventTypes ?? EventTypeRegistry.Default();
        _eventTypes = eventTypes.Names.ToDictionary(name => name, eventTypes.Get, StringComparer.Ordinal);
        _portal = config.Portal ?? new PortalConfig();
        _portal.Validate();

        var timeoutMs = _defaultOptions.TimeoutMs ?? HttpRequester.DefaultTimeoutMs;
        _transport = new IndexerTransport(config.Endpoint, config.Auth, timeoutMs);
    }

    /// <summary>Canonicalize and hash a record without sending it.</summary>
    /// <returns>the 32-byte Keccak-256 digest; <see cref="Keccak256Hasher.ToHex"/> gives its 0x-hex form</returns>
    /// <exception cref="ConfigException">no dataType is given here or in config</exception>
    /// <exception cref="CanonicalizationException">rawData cannot be canonicalized</exception>
    public byte[] Hash(string rawData, SendOptions? options = null) => Hash(EncodeRawData(rawData), options);

    /// <inheritdoc cref="Hash(string, SendOptions?)"/>
    public byte[] Hash(byte[] rawData, SendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawData);

        var dataType = options?.DataType ?? _defaultOptions.DataType
            ?? throw new ConfigException("dataType is required, either in the config \"options\" or per call");

        return Hashes.Record(rawData, dataType);
    }

    /// <summary>
    /// Validate one record of a configured event type and send it via
    /// POST /api/anchors?eventType=... . The SDK builds no tree and derives no
    /// key: roots, keys and ids come from the Indexer's answer.
    /// </summary>
    /// <param name="eventType">a name registered in the config's <see cref="TracingSdkConfig.EventTypes"/></param>
    /// <param name="fields">the request fields by name. bytes32 takes 0x hex or a 32-byte
    /// byte[]; uint64 takes any non-negative integer type or a BigInteger; bytes32[] takes
    /// any list of those; bytes takes 0x hex or a byte[]</param>
    /// <param name="options">per-call timeoutMs; a Merkle type answers only once confirmed
    /// on chain, so give it a timeout well above the default 10 s</param>
    /// <param name="cancellationToken">cancels the request</param>
    /// <exception cref="ConfigException">the event type is unknown, or a field is unknown, missing or invalid</exception>
    /// <exception cref="IndexerException">the Indexer refused the request; <see cref="IndexerException.ErrorCode"/> says why</exception>
    /// <exception cref="TransportException">the request failed</exception>
    public async Task<AnchorResult> AnchorAsync(
        string eventType,
        IReadOnlyDictionary<string, object?> fields,
        SendOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var definition = EventType(eventType);
        var request = FieldValidator.BuildRequest(definition, fields);
        var answer = await _transport.PostEventAsync(definition.Name, request, options?.TimeoutMs, cancellationToken).ConfigureAwait(false);

        return new AnchorResult(definition.Name, answer.StatusCode, answer.Body, answer.Json, request);
    }

    /// <inheritdoc cref="AnchorAsync(string, IReadOnlyDictionary{string, object?}, SendOptions?, CancellationToken)"/>
    public Task<AnchorResult> AnchorAsync(string eventType, JsonObject fields, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);

        return AnchorAsync(eventType, fields.ToDictionary(p => p.Key, p => (object?)p.Value), options, cancellationToken);
    }

    /// <summary>
    /// Hash one record the way the event type says: for a Merkle type a leaf
    /// (keccak256(leafPrefix || canonical bytes), see <see cref="Hashes.Leaf(string, DataType, byte)"/>),
    /// otherwise a record hash (<see cref="Hashes.Record(string, DataType)"/>).
    /// </summary>
    /// <remarks>The dataType is the event type's <c>hashing.dataType</c>, else the
    /// per-call options', else the config's.</remarks>
    /// <exception cref="ConfigException">the event type is unknown, or no dataType is set anywhere</exception>
    /// <exception cref="CanonicalizationException">rawData cannot be canonicalized</exception>
    public byte[] HashFor(string eventType, string rawData, SendOptions? options = null) => HashFor(eventType, EncodeRawData(rawData), options);

    /// <inheritdoc cref="HashFor(string, string, SendOptions?)"/>
    public byte[] HashFor(string eventType, byte[] rawData, SendOptions? options = null)
    {
        var definition = EventType(eventType);
        var dataType = definition.Hashing?.DataType ?? options?.DataType ?? _defaultOptions.DataType
            ?? throw new ConfigException($"dataType is required, either in event type \"{definition.Name}\" hashing, the config \"options\" or per call");

        return definition.Anchoring.Mode == AnchoringMode.Merkle
            ? Hashes.Leaf(rawData, dataType, definition.Hashing?.LeafPrefix ?? Hashes.DefaultLeafPrefix)
            : Hashes.Record(rawData, dataType);
    }

    /// <summary>The leaves of one request of a Merkle type, in the order given.</summary>
    /// <inheritdoc cref="HashFor(string, string, SendOptions?)"/>
    public IReadOnlyList<byte[]> LeavesFor(string eventType, IEnumerable<string> rawData, SendOptions? options = null) =>
        LeavesFor(eventType, rawData.Select(EncodeRawData), options);

    /// <inheritdoc cref="LeavesFor(string, IEnumerable{string}, SendOptions?)"/>
    public IReadOnlyList<byte[]> LeavesFor(string eventType, IEnumerable<byte[]> rawData, SendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawData);

        if (EventType(eventType).Anchoring.Mode != AnchoringMode.Merkle)
        {
            throw new ConfigException($"Event type \"{eventType}\" is not a merkle type; hash its record with HashFor");
        }

        return rawData.Select(r => HashFor(eventType, r, options)).ToList();
    }

    /// <summary>
    /// The portal link that verifies an anchored record of a direct
    /// (non-Merkle) type: the link fields of the request and the derived key
    /// when the type has one. It carries no ticketId: that belongs to leaf links.
    /// </summary>
    /// <param name="result">what <see cref="AnchorAsync(string, IReadOnlyDictionary{string, object?}, SendOptions?, CancellationToken)"/> returned</param>
    /// <exception cref="ConfigException">the type is a Merkle type
    /// (use <see cref="VerificationLinks"/>), or a derived key is missing from the answer</exception>
    public string VerificationLink(AnchorResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var definition = EventType(result.EventType);

        if (definition.Anchoring.Mode == AnchoringMode.Merkle)
        {
            throw new ConfigException($"Event type \"{definition.Name}\" anchors leaves; build one link per leaf with VerificationLinks");
        }

        var fields = LinkFieldsOf(definition, result.Request).ToList();

        if (definition.Anchoring.Key == KeyMode.Derived)
        {
            var key = result.Key ?? throw new ConfigException($"The answer for \"{definition.Name}\" carries no key to link to");
            fields.Add(new(LinkOf(definition).KeyParam, FieldValidator.Bytes32(key, "key")));
        }

        return PortalLink.Build(_portal, definition.Name, fields, ticketId: null);
    }

    /// <summary>
    /// One portal link per leaf of an anchored Merkle request, in leaf order.
    /// Link k carries the link fields, the sequence field set to the leaf's own
    /// number (sequence + k), and the leaf itself.
    /// </summary>
    /// <param name="result">what AnchorAsync returned for a Merkle type</param>
    /// <param name="ticketIdFor">your opaque id of leaf k (e.g. the ticket's id), put after '#'; null for none</param>
    /// <exception cref="ConfigException">the type is not a Merkle type</exception>
    public IReadOnlyList<string> VerificationLinks(AnchorResult result, Func<int, string?>? ticketIdFor = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var definition = EventType(result.EventType);

        if (definition.Anchoring.Mode != AnchoringMode.Merkle)
        {
            throw new ConfigException($"Event type \"{definition.Name}\" is not a merkle type; use VerificationLink");
        }

        var leaves = (JsonArray)result.Request[definition.LeavesField!.Name]!;
        var sequenceField = definition.SequenceField?.Name;
        var firstNumber = sequenceField is not null && result.Request[sequenceField] is JsonValue first ? first.GetValue<ulong>() : 0;

        return leaves.Select((leaf, k) =>
        {
            var fields = LinkFieldsOf(definition, result.Request)
                .Select(p => p.Key == sequenceField ? new KeyValuePair<string, string>(p.Key, (firstNumber + (ulong)k).ToString(System.Globalization.CultureInfo.InvariantCulture)) : p)
                .Append(new(LinkOf(definition).LeafParam, leaf!.GetValue<string>()));

            return PortalLink.Build(_portal, definition.Name, fields, ticketIdFor?.Invoke(k));
        }).ToList();
    }

    /// <summary>
    /// A portal link from values kept by the caller, without an AnchorResult:
    /// the type's link fields, plus its key parameter for a derived key, plus
    /// its leaf parameter for a Merkle type (with the sequence field holding
    /// that leaf's own number).
    /// </summary>
    /// <param name="eventType">a registered event type</param>
    /// <param name="fields">the link fields by name, plus the key or leaf parameter</param>
    /// <param name="ticketId">for a Merkle type only: your opaque id of the leaf, put after '#'</param>
    /// <exception cref="ConfigException">a value is unknown, missing or invalid, or a ticketId
    /// is given for a type that is not a Merkle type</exception>
    public string VerificationLink(string eventType, IReadOnlyDictionary<string, object?> fields, string? ticketId = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var definition = EventType(eventType);
        var link = LinkOf(definition);
        var where = $"Event type \"{definition.Name}\" link";

        if (ticketId is not null && definition.Anchoring.Mode != AnchoringMode.Merkle)
        {
            throw new ConfigException($"{where}: a ticketId applies only to the leaves of a merkle type");
        }
        var linkFields = definition.LinkFields;
        var extra = new List<string>();

        if (definition.Anchoring.Key == KeyMode.Derived)
        {
            extra.Add(link.KeyParam);
        }

        if (definition.Anchoring.Mode == AnchoringMode.Merkle)
        {
            extra.Add(link.LeafParam);
        }

        foreach (var name in fields.Keys.Where(n => !linkFields.Any(f => f.Name == n) && !extra.Contains(n)))
        {
            throw new ConfigException($"{where}: unknown field \"{name}\"; expected: {string.Join(", ", linkFields.Select(f => f.Name).Concat(extra))}");
        }

        object Value(string name, bool required) =>
            fields.TryGetValue(name, out var value) && value is not null
                ? value
                : required ? throw new ConfigException($"{where}: missing field \"{name}\"") : null!;

        var pairs = new List<KeyValuePair<string, string>>();

        foreach (var field in linkFields)
        {
            if (Value(field.Name, field.Required) is { } value)
            {
                pairs.Add(new(field.Name, Format(FieldValidator.Normalize(definition, field, value, where))));
            }
        }

        pairs.AddRange(extra.Select(name => new KeyValuePair<string, string>(name, FieldValidator.Bytes32(Value(name, true), $"{where}: field \"{name}\""))));

        return PortalLink.Build(_portal, definition.Name, pairs, ticketId);
    }

    public void Dispose()
    {
        _transport.Dispose();
    }

    /// <summary>Swap the Indexer transport for a test double. Test-only.</summary>
    internal void SetTransportForTesting(IIndexerTransport transport)
    {
        _transport.Dispose();
        _transport = transport;
    }

    /// <summary>
    /// UTF-8 bytes of a string. A lone surrogate has no UTF-8 encoding, so it
    /// is rejected rather than silently hashed as U+FFFD.
    /// </summary>
    /// <exception cref="CanonicalizationException">the string holds an unpaired surrogate</exception>
    internal static byte[] EncodeRawData(string rawData)
    {
        ArgumentNullException.ThrowIfNull(rawData);

        try
        {
            return StrictUtf8.GetBytes(rawData);
        }
        catch (EncoderFallbackException e)
        {
            throw new CanonicalizationException("rawData holds an unpaired UTF-16 surrogate and has no UTF-8 encoding", e);
        }
    }

    /// <exception cref="ConfigException">the event type is not registered</exception>
    private EventTypeDefinition EventType(string eventType) =>
        eventType is not null && _eventTypes.TryGetValue(eventType, out var definition)
            ? definition
            : throw new ConfigException($"Unknown event type \"{eventType}\"; registered: {string.Join(", ", _eventTypes.Keys.Order(StringComparer.Ordinal))}");

    private static LinkDefinition LinkOf(EventTypeDefinition definition) => definition.Link ?? new LinkDefinition();

    /// <summary>The definition's link fields present in a normalized request, as link text.</summary>
    private static IEnumerable<KeyValuePair<string, string>> LinkFieldsOf(EventTypeDefinition definition, JsonObject request) =>
        definition.LinkFields
            .Where(f => request[f.Name] is not null)
            .Select(f => new KeyValuePair<string, string>(f.Name, Format(request[f.Name]!)));

    /// <summary>A normalized scalar as it appears in a link: strings as they are, integers in decimal.</summary>
    private static string Format(JsonNode node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : node.ToJsonString();

    private static void ValidateAuth(AuthConfig auth)
    {
        static void Require(string? value, string key)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ConfigException($"Missing required config key \"{key}\"");
            }
        }

        switch (auth)
        {
            case AuthConfig.ApiTokenAuth apiToken:
                Require(apiToken.Token, "token");
                break;
            case AuthConfig.BasicAuth basic:
                Require(basic.Username, "username");
                Require(basic.Password, "password");
                break;
            case AuthConfig.MtlsAuth mtls:
                Require(mtls.Cert, "cert");
                Require(mtls.Key, "key");
                break;
        }
    }
}
