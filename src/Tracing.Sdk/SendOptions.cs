namespace Tracing.Sdk;

/// <summary>How a record is normalized before hashing.</summary>
public enum DataType
{
    /// <summary>RFC 8785 JSON Canonicalization Scheme.</summary>
    Json,

    /// <summary>Exclusive XML Canonicalization 1.0, without comments.</summary>
    Xml,

    /// <summary>No canonicalization: the bytes are hashed exactly as given.</summary>
    Raw,
}

/// <summary>
/// Per-call overrides for Send, SendBatch, Hash, QueryByHash, and Verify.
/// Anything left unset falls back to the value the SDK was constructed with.
/// Instances are immutable; the <c>With*</c> methods return a modified copy.
/// </summary>
public sealed class SendOptions
{
    /// <param name="dataType">how records are canonicalized before hashing</param>
    /// <param name="timeoutMs">how long a single HTTP request may take, in
    /// milliseconds; null falls back to the transport default</param>
    /// <param name="rpcUrl">JSON-RPC endpoint of a chain node, used by Verify
    /// to read the anchor event back from chain</param>
    /// <exception cref="ConfigException">timeoutMs is not positive, or rpcUrl is blank</exception>
    public SendOptions(DataType? dataType = null, int? timeoutMs = null, string? rpcUrl = null)
    {
        DataType = dataType;
        TimeoutMs = AssertTimeoutMs(timeoutMs);
        RpcUrl = NormalizeRpcUrl(rpcUrl);
    }

    public DataType? DataType { get; }

    public int? TimeoutMs { get; }

    public string? RpcUrl { get; }

    public static SendOptions ForDataType(DataType dataType) => new(dataType);

    /// <exception cref="ConfigException">timeoutMs is not positive</exception>
    public static SendOptions ForTimeoutMs(int timeoutMs) => new(null, timeoutMs);

    /// <exception cref="ConfigException">rpcUrl is blank</exception>
    public static SendOptions ForRpcUrl(string rpcUrl) => new(null, null, rpcUrl);

    public SendOptions WithDataType(DataType? dataType) => new(dataType, TimeoutMs, RpcUrl);

    /// <exception cref="ConfigException">timeoutMs is not positive</exception>
    public SendOptions WithTimeoutMs(int? timeoutMs) => new(DataType, timeoutMs, RpcUrl);

    /// <exception cref="ConfigException">rpcUrl is blank</exception>
    public SendOptions WithRpcUrl(string? rpcUrl) => new(DataType, TimeoutMs, rpcUrl);

    private static int? AssertTimeoutMs(int? timeoutMs)
    {
        if (timeoutMs is <= 0)
        {
            throw new ConfigException("timeoutMs must be a positive number of milliseconds");
        }

        return timeoutMs;
    }

    private static string? NormalizeRpcUrl(string? rpcUrl)
    {
        if (rpcUrl is null)
        {
            return null;
        }

        var trimmed = rpcUrl.Trim();

        if (trimmed.Length == 0)
        {
            throw new ConfigException("rpcUrl must be a non-empty URL");
        }

        return trimmed;
    }
}
