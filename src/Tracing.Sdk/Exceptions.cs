using System.Text.Json;

namespace Tracing.Sdk;

/// <summary>
/// Base class for every exception the SDK throws, so a single <c>catch</c>
/// can cover them all.
/// </summary>
public class TracingSdkException : Exception
{
    public TracingSdkException(string message)
        : base(message)
    {
    }

    public TracingSdkException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Missing or invalid configuration, options, or call arguments.</summary>
public class ConfigException : TracingSdkException
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The raw data cannot be canonicalized for the chosen data type.</summary>
public class CanonicalizationException : TracingSdkException
{
    public CanonicalizationException(string message)
        : base(message)
    {
    }

    public CanonicalizationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An HTTP / JSON-RPC request failed, timed out, or got a non-2xx answer.</summary>
public class TransportException : TracingSdkException
{
    public TransportException(string message)
        : base(message)
    {
    }

    public TransportException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The Indexer answered non-2xx. Carries the fields of its
/// <c>{ "error": "&lt;CODE&gt;", "field", "index", "message", "batchId" }</c>
/// body, so a caller can branch on <see cref="ErrorCode"/> — for example
/// resend the same batch on <c>CONFIRMATION_TIMEOUT</c>.
/// </summary>
public class IndexerException : TransportException
{
    public IndexerException(string message, int statusCode, string body, JsonElement? json)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;

        if (json is { ValueKind: JsonValueKind.Object } error)
        {
            ErrorCode = StringOf(error, "error");
            Field = StringOf(error, "field");
            ServerMessage = StringOf(error, "message");
            Index = error.TryGetProperty("index", out var index) && index.TryGetInt32(out var i) ? i : null;
            BatchId = error.TryGetProperty("batchId", out var batchId) && batchId.TryGetInt64(out var b) ? b : null;
        }
    }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>The raw response body.</summary>
    public string Body { get; }

    /// <summary>The <c>error</c> code, e.g. <c>DRAW_NOT_LOCKED</c>; null when the body has none.</summary>
    public string? ErrorCode { get; }

    /// <summary>The request field the error is about, when the Indexer names one.</summary>
    public string? Field { get; }

    /// <summary>The position inside an array field (or a batch) the error is about.</summary>
    public int? Index { get; }

    /// <summary>The Indexer's human-readable <c>message</c>.</summary>
    public string? ServerMessage { get; }

    /// <summary>The stored tree the error is about (<c>ANCHOR_REVERTED</c>).</summary>
    public long? BatchId { get; }

    private static string? StringOf(JsonElement error, string name) =>
        error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
