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
