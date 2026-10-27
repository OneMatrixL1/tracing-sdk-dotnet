using System.Text.Json;
using Tracing.Sdk.Events;

namespace Tracing.Sdk;

/// <summary>Constructor configuration for <see cref="TracingSdk"/>.</summary>
public sealed class TracingSdkConfig
{
    /// <summary>Indexer base URL. A trailing "/" is trimmed.</summary>
    public required string Endpoint { get; init; }

    /// <summary>How requests to the Indexer authenticate.</summary>
    public required AuthConfig Auth { get; init; }

    /// <summary>Defaults for every call that doesn't pass its own SendOptions.</summary>
    public SendOptions? Options { get; init; }

    /// <summary>
    /// The event types <see cref="TracingSdk.AnchorAsync(string, IReadOnlyDictionary{string, object?}, SendOptions?, CancellationToken)"/>
    /// accepts. Null means only the default <c>anchor</c> type. The SDK keeps the
    /// types registered when it is constructed; registering more later has no effect on it.
    /// </summary>
    public EventTypeRegistry? EventTypes { get; init; }

    /// <summary>Where verification links point; null means <see cref="PortalConfig.DefaultBaseUrl"/> with the default parameter names.</summary>
    public PortalConfig? Portal { get; init; }

    /// <summary>
    /// Read a config from JSON:
    /// <code>
    /// {
    ///   "endpoint": "https://indexer.example.com",
    ///   "auth": { "type": "apiToken", "token": "..." },
    ///   "options": { "dataType": "json", "timeoutMs": 60000 },
    ///   "portal": { "baseUrl": "https://integrity.vmatrix.com/v1/" },
    ///   "eventTypes": [ ... ]
    /// }
    /// </code>
    /// <c>auth.type</c> is <c>apiToken</c> (<c>token</c>), <c>basic</c>
    /// (<c>username</c>, <c>password</c>) or <c>mtls</c> (<c>cert</c>, <c>key</c>,
    /// <c>caCert</c>, <c>passphrase</c>). <c>options.rpcUrl</c> and
    /// <c>portal.paramNames</c> (<c>eventType</c>, <c>ticketId</c>) are
    /// optional. <c>eventTypes</c> takes the shape of <see cref="EventTypeRegistry"/>;
    /// when <paramref name="eventTypes"/> is given it wins over it.
    /// </summary>
    /// <exception cref="ConfigException">the JSON is malformed or a value is invalid</exception>
    public static TracingSdkConfig FromJson(string json, EventTypeRegistry? eventTypes = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("The SDK config must be a JSON object");
            }

            return new TracingSdkConfig
            {
                Endpoint = Text(root, "endpoint") ?? throw new ConfigException("Missing required config key \"endpoint\""),
                Auth = ParseAuth(Member(root, "auth") ?? throw new ConfigException("Missing required config key \"auth\"")),
                Options = Member(root, "options") is { } options ? ParseOptions(options) : null,
                Portal = Member(root, "portal") is { } portal ? ParsePortal(portal) : null,
                EventTypes = eventTypes
                    ?? (Member(root, "eventTypes") is { } types ? new EventTypeRegistry().Register(EventTypeRegistry.ParseEventTypes(types)) : null),
            };
        }
        catch (JsonException e)
        {
            throw new ConfigException("The SDK config is not valid JSON: " + e.Message, e);
        }
    }

    /// <inheritdoc cref="FromJson(string, EventTypeRegistry?)"/>
    public static TracingSdkConfig FromJsonFile(string path, EventTypeRegistry? eventTypes = null)
    {
        try
        {
            return FromJson(File.ReadAllText(path), eventTypes);
        }
        catch (IOException e)
        {
            throw new ConfigException($"SDK config file cannot be read: {path}: {e.Message}", e);
        }
    }

    private static AuthConfig ParseAuth(JsonElement auth)
    {
        string Required(string name) => Text(auth, name) ?? throw new ConfigException($"Missing required config key \"auth.{name}\"");

        return Text(auth, "type") switch
        {
            "apiToken" => AuthConfig.ApiToken(Required("token")),
            "basic" => AuthConfig.Basic(Required("username"), Required("password")),
            "mtls" => AuthConfig.Mtls(Required("cert"), Required("key"), Text(auth, "caCert"), Text(auth, "passphrase")),
            var type => throw new ConfigException($"auth.type must be \"apiToken\", \"basic\" or \"mtls\", got \"{type}\""),
        };
    }

    private static SendOptions ParseOptions(JsonElement options)
    {
        DataType? dataType = Text(options, "dataType") switch
        {
            null => null,
            var name when System.Enum.TryParse<DataType>(name, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed) => parsed,
            var name => throw new ConfigException($"options.dataType must be json, xml or raw, got \"{name}\""),
        };
        int? timeoutMs = Member(options, "timeoutMs") switch
        {
            null => null,
            { } value when value.TryGetInt32(out var ms) => ms,
            _ => throw new ConfigException("options.timeoutMs must be an integer"),
        };

        return new SendOptions(dataType, timeoutMs, Text(options, "rpcUrl"));
    }

    private static PortalConfig ParsePortal(JsonElement portal)
    {
        var defaults = new PortalParamNames();
        var names = Member(portal, "paramNames");

        var config = new PortalConfig
        {
            BaseUrl = Text(portal, "baseUrl") ?? PortalConfig.DefaultBaseUrl,
            ParamNames = names is { } n
                ? new PortalParamNames(Text(n, "eventType") ?? defaults.EventType, Text(n, "ticketId") ?? defaults.TicketId)
                : defaults,
        };
        config.Validate();

        return config;
    }

    private static JsonElement? Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? Text(JsonElement element, string name) => Member(element, name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        _ => throw new ConfigException($"Config key \"{name}\" must be a string"),
    };
}

/// <summary>How requests to the Indexer authenticate.</summary>
public abstract record AuthConfig
{
    private AuthConfig()
    {
    }

    /// <summary>Sends the token in the "X-API-Key" header.</summary>
    public static AuthConfig ApiToken(string token) => new ApiTokenAuth(token);

    /// <summary>Sends "Authorization: Basic &lt;base64(username:password)&gt;".</summary>
    public static AuthConfig Basic(string username, string password) => new BasicAuth(username, password);

    /// <summary>
    /// Mutual TLS: presents a PEM client certificate during the TLS handshake
    /// and, when <paramref name="caCert"/> is given, trusts only that CA for the
    /// server certificate. Files are read when the SDK is constructed.
    /// </summary>
    public static AuthConfig Mtls(string cert, string key, string? caCert = null, string? passphrase = null) =>
        new MtlsAuth(cert, key, caCert, passphrase);

    internal sealed record ApiTokenAuth(string Token) : AuthConfig;

    internal sealed record BasicAuth(string Username, string Password) : AuthConfig
    {
        // Keep the password out of the compiler-generated ToString().
        public override string ToString() => $"BasicAuth {{ Username = {Username} }}";
    }

    internal sealed record MtlsAuth(string Cert, string Key, string? CaCert, string? Passphrase) : AuthConfig
    {
        public override string ToString() => $"MtlsAuth {{ Cert = {Cert}, Key = {Key}, CaCert = {CaCert} }}";
    }
}

/// <summary>One record for <see cref="TracingSdk.SendBatchAsync"/>.</summary>
public sealed class BatchRecord
{
    /// <param name="rawData">the record; hashed as its UTF-8 bytes</param>
    /// <param name="signingTime">passed through to the Indexer untouched</param>
    public BatchRecord(string rawData, object signingTime)
    {
        RawData = rawData is null ? null! : TracingSdk.EncodeRawData(rawData);
        SigningTime = signingTime;
    }

    /// <param name="rawData">the record's bytes</param>
    /// <param name="signingTime">passed through to the Indexer untouched</param>
    public BatchRecord(byte[] rawData, object signingTime)
    {
        RawData = rawData;
        SigningTime = signingTime;
    }

    public byte[] RawData { get; }

    public object SigningTime { get; }
}

/// <summary>What the Indexer answered.</summary>
/// <param name="StatusCode">HTTP status code</param>
/// <param name="Body">the raw response body</param>
/// <param name="Json">the body parsed as JSON, or null when it is not JSON</param>
/// <param name="RecordCount">how many records the request carried</param>
public sealed record IndexerResponse(int StatusCode, string Body, JsonElement? Json, int RecordCount);

/// <summary>The result of sending one record.</summary>
/// <param name="Hash">the record's 32-byte Keccak-256 digest; <see cref="Hash.Keccak256Hasher.ToHex"/> gives its 0x-hex form</param>
/// <param name="Response">the Indexer's answer; shared by every record of a batch</param>
public sealed record SendResult(byte[] Hash, IndexerResponse Response);

/// <summary>An anchor looked up by hash.</summary>
/// <param name="Hash">the record hash, as echoed back by the Indexer</param>
/// <param name="Proof">every on-chain proof the record was anchored by; with ProofType "transactionHash", the 32-byte transaction hashes</param>
/// <param name="ProofType">how each proof is resolved on chain; pass it to Verify as its mode</param>
public sealed record QueryResult(byte[] Hash, IReadOnlyList<byte[]> Proof, string ProofType);
