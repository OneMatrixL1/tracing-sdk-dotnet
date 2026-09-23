namespace Tracing.Sdk.Examples;

/// <summary>
/// Where every example connects. Replace these with the values you were
/// given before running an example.
/// </summary>
internal static class ExampleSettings
{
    /// <summary>Your Indexer's base URL.</summary>
    public const string Endpoint = "http://localhost:3000";

    /// <summary>Your Indexer API token.</summary>
    public const string ApiToken = "your-api-token";

    /// <summary>JSON-RPC endpoint of your chain node; only VerifyExample uses it.</summary>
    public const string RpcUrl = "http://localhost:8545";

    /// <summary>The current time as a Unix timestamp, used as each record's signingTime.</summary>
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
