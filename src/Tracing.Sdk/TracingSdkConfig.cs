using System.Text.Json;

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
