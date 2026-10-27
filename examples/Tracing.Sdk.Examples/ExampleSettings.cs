using Tracing.Sdk.Events;

namespace Tracing.Sdk.Examples;

/// <summary>
/// Where every example connects. Replace these with the values you were
/// given before running an example.
/// </summary>
internal static class ExampleSettings
{
    /// <summary>Your Indexer's base URL.</summary>
    public const string Endpoint = "http://localhost:8080";

    /// <summary>Your Indexer API token; the TRACING_API_KEY environment variable wins, so the key stays out of the code.</summary>
    public static readonly string ApiToken = Environment.GetEnvironmentVariable("TRACING_API_KEY") ?? "your-api-token";

    /// <summary>JSON-RPC endpoint of your chain node; only VerifyExample uses it.</summary>
    public const string RpcUrl = "http://localhost:8545";

    /// <summary>The current time as a Unix timestamp, used as each record's signingTime.</summary>
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>A file of config/, copied next to the example binary.</summary>
    public static string ConfigPath(string name) => Path.Combine(AppContext.BaseDirectory, "config", name);

    /// <summary>
    /// The SDK the tickets examples use: endpoint, auth, options and portal
    /// from config/tracing.example.json, the six event types from
    /// config/tickets-events.json. Edit those two files, not this code.
    /// </summary>
    public static TracingSdk CreateTicketsSdk() => CreateSdk("tickets-events.json");

    /// <summary>
    /// An SDK from config/tracing.example.json and the given event-type file.
    /// TRACING_API_KEY, when set, replaces the file's auth.
    /// </summary>
    public static TracingSdk CreateSdk(string eventTypesFile)
    {
        var config = TracingSdkConfig.FromJsonFile(ConfigPath("tracing.example.json"), EventTypeRegistry.FromJsonFile(ConfigPath(eventTypesFile)));

        if (Environment.GetEnvironmentVariable("TRACING_API_KEY") is { Length: > 0 } key)
        {
            config = new TracingSdkConfig
            {
                Endpoint = config.Endpoint,
                Auth = AuthConfig.ApiToken(key),
                Options = config.Options,
                EventTypes = config.EventTypes,
                Portal = config.Portal,
            };
        }

        return new TracingSdk(config);
    }

    /// <summary>The draw every tickets example works on. Keep one spelling per draw.</summary>
    public const string Draw = "draw-1";
}
