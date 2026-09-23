using System.Text.Json;
using Tracing.Sdk.Hash;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>Send a single record right away via POST /api/anchors instead of batching.</summary>
internal static class SingleSendExample
{
    public static async Task RunAsync()
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = SendOptions.ForDataType(DataType.Raw), // default for every send/sendBatch
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        // Override the default data type for this send.
        var result = await sdk.SendAsync(JsonSerializer.Serialize(new { orderId = 1 }), Now(), SendOptions.ForDataType(DataType.Json));

        Console.WriteLine($"[sent] {Keccak256Hasher.ToHex(result.Hash)} -> HTTP {result.Response.StatusCode} {result.Response.Body}");
    }
}
