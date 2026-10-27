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
