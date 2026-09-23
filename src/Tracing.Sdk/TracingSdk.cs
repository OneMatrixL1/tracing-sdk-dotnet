using System.Text;
using System.Text.Json;
using Tracing.Sdk.Canonicalize;
using Tracing.Sdk.Hash;
using Tracing.Sdk.Rpc;
using Tracing.Sdk.Transport;
using Tracing.Sdk.Verify;

namespace Tracing.Sdk;

/// <summary>
/// Canonicalizes a record, hashes it with Keccak-256, and sends the resulting
/// { hash, signingTime } entry to the Indexer. Holds HTTP connections; dispose
/// it when done, or keep one instance for the application's lifetime.
/// </summary>
public sealed class TracingSdk : IDisposable
{
    /// <summary>
    /// Verify mode for proofs that are transaction hashes — the only proof
    /// kind today. The mode parameter exists so other proof kinds can be added
    /// without changing Verify's signature.
    /// </summary>
    public const string ModeTransactionHash = "transactionHash";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<DataType, ICanonicalizer> _canonicalizers = new()
    {
        [DataType.Json] = new JsonCanonicalizer(),
        [DataType.Xml] = new XmlCanonicalizer(),
        [DataType.Raw] = new RawCanonicalizer(),
    };

    private readonly SendOptions _defaultOptions;
    private readonly Keccak256Hasher _hasher = new();
    private readonly AnchoredEventDecoder _eventDecoder;
    private IIndexerTransport _transport;
    private IRpcTransport _rpcTransport;

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
        _eventDecoder = new AnchoredEventDecoder(_hasher);

        var timeoutMs = _defaultOptions.TimeoutMs ?? HttpRequester.DefaultTimeoutMs;
        _transport = new IndexerTransport(config.Endpoint, config.Auth, timeoutMs);
        _rpcTransport = new RpcTransport(timeoutMs);
    }

    /// <summary>Canonicalize, hash, and send one record via POST /api/anchors.</summary>
    /// <param name="rawData">the record; hashed as its UTF-8 bytes</param>
    /// <param name="signingTime">passed through to the Indexer untouched</param>
    /// <param name="options">per-call overrides; falls back to config</param>
    /// <param name="cancellationToken">cancels the request</param>
    /// <exception cref="ConfigException">signingTime is null, or no dataType is given here or in config</exception>
    /// <exception cref="CanonicalizationException">rawData cannot be canonicalized</exception>
    /// <exception cref="TransportException">the request failed or the Indexer answered non-2xx</exception>
    public Task<SendResult> SendAsync(string rawData, object signingTime, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync(EncodeRawData(rawData), signingTime, options, cancellationToken);

    /// <inheritdoc cref="SendAsync(string, object, SendOptions?, CancellationToken)"/>
    public async Task<SendResult> SendAsync(byte[] rawData, object signingTime, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (signingTime is null)
        {
            throw new ConfigException("signingTime is required");
        }

        var entry = new AnchorEntry(Hash(rawData, options), signingTime);
        var response = await _transport.SendSingleAsync(entry, options?.TimeoutMs, cancellationToken).ConfigureAwait(false);

        return new SendResult(entry.Hash, response);
    }

    /// <summary>
    /// Canonicalize, hash, and send multiple records in one request via POST
    /// /api/anchors/batch. Every record is validated and hashed before anything
    /// is sent, so one bad record cannot half-anchor a batch.
    /// </summary>
    /// <returns>one result per input record, in input order, all sharing the one response</returns>
    /// <exception cref="ConfigException">a record is null or lacks rawData/signingTime, or no dataType is given here or in config</exception>
    /// <exception cref="CanonicalizationException">a record cannot be canonicalized</exception>
    /// <exception cref="TransportException">the request failed or the Indexer answered non-2xx</exception>
    public async Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IEnumerable<BatchRecord> records,
        SendOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var entries = records
            .Select(record =>
            {
                if (record?.RawData is null || record.SigningTime is null)
                {
                    throw new ConfigException("Each record requires \"rawData\" and \"signingTime\"");
                }

                return new AnchorEntry(Hash(record.RawData, options), record.SigningTime);
            })
            .ToList();

        var response = await _transport.SendBatchAsync(entries, options?.TimeoutMs, cancellationToken).ConfigureAwait(false);

        return entries.Select(entry => new SendResult(entry.Hash, response)).ToList();
    }

    /// <summary>Canonicalize and hash a record without sending it.</summary>
    /// <returns>0x-prefixed Keccak-256 hex</returns>
    /// <exception cref="ConfigException">no dataType is given here or in config</exception>
    /// <exception cref="CanonicalizationException">rawData cannot be canonicalized</exception>
    public string Hash(string rawData, SendOptions? options = null) => Hash(EncodeRawData(rawData), options);

    /// <inheritdoc cref="Hash(string, SendOptions?)"/>
    public string Hash(byte[] rawData, SendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawData);

        var dataType = options?.DataType ?? _defaultOptions.DataType
            ?? throw new ConfigException("dataType is required, either in the config \"options\" or per call");

        return _hasher.Hash(_canonicalizers[dataType].Canonicalize(rawData));
    }

    /// <summary>Look up an anchored record by its hash via GET /api/anchors?hash=...</summary>
    /// <returns>the hash, every proof, and the proofType to pass to Verify as its mode</returns>
    /// <exception cref="ConfigException">hash is empty</exception>
    /// <exception cref="TransportException">the request failed, the Indexer answered non-2xx
    /// (including 404 for a hash never anchored), or the body is not the expected { hash, proof }</exception>
    public async Task<QueryResult> QueryByHashAsync(string hash, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(hash))
        {
            throw new ConfigException("hash is required");
        }

        var answer = await _transport.QueryByHashAsync(hash, options?.TimeoutMs, cancellationToken).ConfigureAwait(false);

        if (answer.StatusCode is < 200 or >= 300)
        {
            throw new TransportException($"Query by hash failed with HTTP {answer.StatusCode}");
        }

        if (answer.Json is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("hash", out var hashElement) || hashElement.ValueKind == JsonValueKind.Null
            || !body.TryGetProperty("proof", out var proofElement) || proofElement.ValueKind != JsonValueKind.Array)
        {
            throw new TransportException("Unexpected response body for query by hash, expected { hash, proof }");
        }

        // Older Indexers answer without a proofType; transaction hashes were
        // the only proof kind then, so that is the safe assumption.
        var proofType = body.TryGetProperty("proofType", out var proofTypeElement) && proofTypeElement.ValueKind != JsonValueKind.Null
            ? AsString(proofTypeElement)
            : ModeTransactionHash;

        return new QueryResult(AsString(hashElement), proofElement.EnumerateArray().Select(AsString).ToList(), proofType);
    }

    /// <summary>
    /// Check a query result against the chain itself: fetch the proof's
    /// transaction logs from the configured JSON-RPC endpoint, ABI-decode the
    /// ones emitted as Anchored(bytes32,uint64), and report whether one of them
    /// carries exactly this data hash.
    /// </summary>
    /// <remarks>
    /// A log matches when its topics[0] equals keccak256 of the event
    /// signature and its decoded bytes32 argument equals dataHash. The bytes32
    /// is read from topics[1] when the argument is indexed and from the log
    /// data otherwise, so both layouts verify.
    /// </remarks>
    /// <param name="dataHash">the record hash, as returned by Hash/Send</param>
    /// <param name="proof">with <see cref="ModeTransactionHash"/>, one of the proofs from QueryByHash</param>
    /// <param name="mode">how to resolve the proof; pass QueryByHash's ProofType</param>
    /// <param name="options">per-call rpcUrl / timeoutMs; an rpcUrl must be given here or in config</param>
    /// <param name="cancellationToken">cancels the request</param>
    /// <returns>true when the transaction anchored this data hash</returns>
    /// <exception cref="ConfigException">dataHash or proof is empty or malformed, the mode is unsupported, or no rpcUrl is configured</exception>
    /// <exception cref="TransportException">the RPC call failed or the node does not know the transaction</exception>
    public async Task<bool> VerifyAsync(
        string dataHash,
        string proof,
        string mode = ModeTransactionHash,
        SendOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (mode != ModeTransactionHash)
        {
            throw new ConfigException($"Unsupported verify mode \"{mode}\", expected \"{ModeTransactionHash}\"");
        }

        var normalizedDataHash = AnchoredEventDecoder.NormalizeHash(dataHash, "dataHash");
        var txHash = AnchoredEventDecoder.NormalizeHash(proof, "proof");
        var rpcUrl = options?.RpcUrl ?? _defaultOptions.RpcUrl
            ?? throw new ConfigException("rpcUrl is required to verify, either in the config \"options\" or per call");

        var receipt = await _rpcTransport.GetTransactionReceiptAsync(rpcUrl, txHash, options?.TimeoutMs, cancellationToken).ConfigureAwait(false)
            ?? throw new TransportException($"Transaction {txHash} was not found on the RPC endpoint");

        if (!receipt.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return logs.EnumerateArray().Any(log => _eventDecoder.Decode(log)?.DataHash == normalizedDataHash);
    }

    public void Dispose()
    {
        _transport.Dispose();
        _rpcTransport.Dispose();
    }

    /// <summary>Swap the Indexer transport for a test double. Test-only.</summary>
    internal void SetTransportForTesting(IIndexerTransport transport)
    {
        _transport.Dispose();
        _transport = transport;
    }

    /// <summary>Swap the JSON-RPC transport for a test double. Test-only.</summary>
    internal void SetRpcTransportForTesting(IRpcTransport rpcTransport)
    {
        _rpcTransport.Dispose();
        _rpcTransport = rpcTransport;
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

    private static string AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
}
