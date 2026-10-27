using Tracing.Sdk.Events;

namespace Tracing.Sdk.Examples;

/// <summary>
/// Where every example connects. Replace these with the values you were
/// given before running an example. Only the event types come from files:
/// config/tickets-events.json and config/custom-events.example.json.
/// </summary>
internal static class ExampleSettings
{
    /// <summary>Your Indexer's base URL.</summary>
    public const string Endpoint = "http://localhost:8080";

    /// <summary>Your Indexer API token; set TRACING_API_KEY to keep it out of the code.</summary>
    public static readonly string ApiToken = Environment.GetEnvironmentVariable("TRACING_API_KEY") ?? "your-api-token";

    /// <summary>The draw every tickets example works on. Keep one spelling per draw.</summary>
    public const string Draw = "draw-1";

    /// <summary>A file of config/, copied next to the example binary.</summary>
    public static string ConfigPath(string name) => Path.Combine(AppContext.BaseDirectory, "config", name);

    /// <summary>The SDK the tickets examples use, with the six types of config/tickets-events.json.</summary>
    public static TracingSdk CreateTicketsSdk() => CreateSdk("tickets-events.json");

    /// <summary>An SDK whose event types come from the given file of config/.</summary>
    public static TracingSdk CreateSdk(string eventTypesFile) => new(new TracingSdkConfig
    {
        Endpoint = Endpoint,
        Auth = AuthConfig.ApiToken(ApiToken),

        // Tickets and records are JSON, canonicalized (RFC 8785) before hashing.
        // A batch answers only once it is confirmed on chain: allow a long timeout.
        Options = new SendOptions(DataType.Json, 60_000),

        // Where verification links point; this is the default, shown for completeness.
        Portal = new PortalConfig { BaseUrl = PortalConfig.DefaultBaseUrl },

        EventTypes = EventTypeRegistry.FromJsonFile(ConfigPath(eventTypesFile)),
    });
}
