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
    /// Verify mode for proofs that are transaction hashes: each proof is a
    /// transaction whose Anchored event carries the record hash itself.
    /// </summary>
    public const string ModeTransactionHash = "transactionHash";

    /// <summary>
    /// Verify mode for Merkle proofs: the proof is one array whose first
    /// element is the transaction hash and whose remaining elements are the
    /// record's sibling hashes, leaf to root. The transaction's Anchored event
    /// carries the Merkle root (see <see cref="MerkleProof"/> for the tree
    /// layout).
    /// </summary>
    public const string ModeMerkleProof = "merkleProof";

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

        var hash = Hash(rawData, options);
        var entry = new AnchorEntry(Keccak256Hasher.ToHex(hash), signingTime);
        var response = await _transport.SendSingleAsync(entry, options?.TimeoutMs, cancellationToken).ConfigureAwait(false);

        return new SendResult(hash, response);
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

        var hashes = records
            .Select(record =>
            {
                if (record?.RawData is null || record.SigningTime is null)
                {
                    throw new ConfigException("Each record requires \"rawData\" and \"signingTime\"");
                }

                return (Hash: Hash(record.RawData, options), record.SigningTime);
            })
            .ToList();

        var entries = hashes.Select(h => new AnchorEntry(Keccak256Hasher.ToHex(h.Hash), h.SigningTime)).ToList();
        var response = await _transport.SendBatchAsync(entries, options?.TimeoutMs, cancellationToken).ConfigureAwait(false);

        return hashes.Select(h => new SendResult(h.Hash, response)).ToList();
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

        return _hasher.Hash(_canonicalizers[dataType].Canonicalize(rawData));
    }

    /// <summary>Look up an anchored record by its hash, as returned by Hash/Send.</summary>
    /// <inheritdoc cref="QueryByHashAsync(string, SendOptions?, CancellationToken)"/>
    public Task<QueryResult> QueryByHashAsync(byte[] hash, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        QueryByHashAsync(ToHexOrEmpty(hash), options, cancellationToken);

    /// <summary>Look up an anchored record by its hash via GET /api/anchors?hash=...</summary>
    /// <returns>the hash, every proof, and the proofType to pass to Verify as its mode</returns>
    /// <exception cref="ConfigException">hash is empty</exception>
    /// <exception cref="TransportException">the request failed, the Indexer answered non-2xx
    /// (including 404 for a hash never anchored), or the body is not the expected { hash, proof }
    /// with hex values</exception>
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

        return new QueryResult(
            HexToBytes(hashElement, "hash"),
            proofElement.EnumerateArray().Select(p => HexToBytes(p, "proof")).ToList(),
            proofType);
    }

    /// <summary>
    /// Verify a record against the chain with a query result's whole proof
    /// field, whatever its mode — pass QueryByHash's Hash, Proof, and ProofType.
    /// </summary>
    /// <remarks>
    /// <para>With <see cref="ModeMerkleProof"/>, <paramref name="proof"/> is
    /// [transaction hash, sibling hashes…]: the siblings fold the record hash
    /// into a Merkle root (<see cref="MerkleProof.ComputeRoot"/>), and the
    /// transaction must carry an Anchored event with that root.</para>
    /// <para>With <see cref="ModeTransactionHash"/>, <paramref name="proof"/>
    /// lists transactions, checked in order until one carries an Anchored event
    /// with the record hash itself.</para>
    /// </remarks>
    /// <param name="dataHash">the record hash, as returned by Hash/Send or QueryByHash</param>
    /// <param name="proof">QueryByHash's Proof</param>
    /// <param name="mode">QueryByHash's ProofType</param>
    /// <param name="options">per-call rpcUrl / timeoutMs; an rpcUrl must be given here or in config</param>
    /// <param name="cancellationToken">cancels the request</param>
    /// <returns>true when the chain confirms the record was anchored</returns>
    /// <exception cref="ConfigException">dataHash or a proof element is not 32 bytes, the proof is
    /// empty, the mode is unsupported, or no rpcUrl is configured</exception>
    /// <exception cref="TransportException">an RPC call failed or the node does not know a transaction</exception>
    public Task<bool> VerifyAsync(
        byte[] dataHash,
        IReadOnlyList<byte[]> proof,
        string mode,
        SendOptions? options = null,
        CancellationToken cancellationToken = default) =>
        VerifyCoreAsync(ToHexOrEmpty(dataHash), proof?.Select(ToHexOrEmpty).ToList(), mode, options, cancellationToken);

    /// <inheritdoc cref="VerifyAsync(byte[], IReadOnlyList{byte[]}, string, SendOptions?, CancellationToken)"/>
    public Task<bool> VerifyAsync(
        string dataHash,
        IReadOnlyList<string> proof,
        string mode,
        SendOptions? options = null,
        CancellationToken cancellationToken = default) =>
        VerifyCoreAsync(dataHash, proof, mode, options, cancellationToken);

    /// <summary>
    /// Verify a record hash against the chain, with the hash and one proof
    /// element as bytes — as Hash/Send and QueryByHash return them.
    /// </summary>
    /// <inheritdoc cref="VerifyAsync(string, string, string, SendOptions?, CancellationToken)"/>
    public Task<bool> VerifyAsync(
        byte[] dataHash,
        byte[] proof,
        string mode = ModeTransactionHash,
        SendOptions? options = null,
        CancellationToken cancellationToken = default) =>
        VerifyCoreAsync(ToHexOrEmpty(dataHash), [ToHexOrEmpty(proof)], mode, options, cancellationToken);

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
    /// data otherwise, so both layouts verify. With <see cref="ModeMerkleProof"/>,
    /// a single proof element is a transaction anchoring a one-leaf tree, whose
    /// root is the data hash itself; use the list overloads for longer proofs.
    /// </remarks>
    /// <param name="dataHash">the record hash, as returned by Hash/Send</param>
    /// <param name="proof">with <see cref="ModeTransactionHash"/>, one of the proofs from QueryByHash</param>
    /// <param name="mode">how to resolve the proof; pass QueryByHash's ProofType</param>
    /// <param name="options">per-call rpcUrl / timeoutMs; an rpcUrl must be given here or in config</param>
    /// <param name="cancellationToken">cancels the request</param>
    /// <returns>true when the transaction anchored this data hash</returns>
    /// <exception cref="ConfigException">dataHash or proof is empty or malformed, the mode is unsupported, or no rpcUrl is configured</exception>
    /// <exception cref="TransportException">the RPC call failed or the node does not know the transaction</exception>
    public Task<bool> VerifyAsync(
        string dataHash,
        string proof,
        string mode = ModeTransactionHash,
        SendOptions? options = null,
        CancellationToken cancellationToken = default) =>
        VerifyCoreAsync(dataHash, [proof], mode, options, cancellationToken);

    private async Task<bool> VerifyCoreAsync(
        string dataHash,
        IReadOnlyList<string>? proof,
        string mode,
        SendOptions? options,
        CancellationToken cancellationToken)
    {
        if (mode is not (ModeTransactionHash or ModeMerkleProof))
        {
            throw new ConfigException($"Unsupported verify mode \"{mode}\", expected \"{ModeTransactionHash}\" or \"{ModeMerkleProof}\"");
        }

        var normalizedDataHash = AnchoredEventDecoder.NormalizeHash(dataHash, "dataHash");

        if (proof is null || proof.Count == 0)
        {
            throw new ConfigException("proof is required");
        }

        var elements = proof.Select((p, i) => AnchoredEventDecoder.NormalizeHash(p, proof.Count == 1 ? "proof" : $"proof[{i}]")).ToList();
        var rpcUrl = options?.RpcUrl ?? _defaultOptions.RpcUrl
            ?? throw new ConfigException("rpcUrl is required to verify, either in the config \"options\" or per call");

        if (mode == ModeMerkleProof)
        {
            // proof = [transaction hash, sibling hashes leaf-to-root]; the
            // transaction anchors the root the siblings fold the leaf into.
            var root = MerkleProof.ComputeRoot(
                Convert.FromHexString(normalizedDataHash[2..]),
                elements.Skip(1).Select(sibling => Convert.FromHexString(sibling[2..])));

            return await AnchorsAsync(rpcUrl, elements[0], Keccak256Hasher.ToHex(root), options, cancellationToken).ConfigureAwait(false);
        }

        foreach (var txHash in elements)
        {
            if (await AnchorsAsync(rpcUrl, txHash, normalizedDataHash, options, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the transaction's receipt holds an Anchored event carrying anchoredHash.</summary>
    /// <exception cref="TransportException">the RPC call failed or the node does not know the transaction</exception>
    private async Task<bool> AnchorsAsync(string rpcUrl, string txHash, string anchoredHash, SendOptions? options, CancellationToken cancellationToken)
    {
        var receipt = await _rpcTransport.GetTransactionReceiptAsync(rpcUrl, txHash, options?.TimeoutMs, cancellationToken).ConfigureAwait(false)
            ?? throw new TransportException($"Transaction {txHash} was not found on the RPC endpoint");

        if (!receipt.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return logs.EnumerateArray().Any(log => _eventDecoder.Decode(log)?.DataHash == anchoredHash);
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

    /// <summary>A digest as hex, or "" for a missing one so the string overloads report it.</summary>
    private static string ToHexOrEmpty(byte[]? bytes) => bytes is null || bytes.Length == 0 ? "" : Keccak256Hasher.ToHex(bytes);

    /// <summary>Decode a hex value from the Indexer's answer, 0x-prefixed or bare.</summary>
    /// <exception cref="TransportException">the value is not a non-empty hex string</exception>
    private static byte[] HexToBytes(JsonElement element, string field)
    {
        var text = element.ValueKind == JsonValueKind.String ? element.GetString()!.Trim() : "";
        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;

        try
        {
            if (digits.Length > 0)
            {
                return Convert.FromHexString(digits);
            }
        }
        catch (FormatException)
        {
            // Odd length or a non-hex character; reported below.
        }

        throw new TransportException($"Unexpected {field} in query by hash response, expected a hex string, got {element.GetRawText()}");
    }
}
