using System.Text.Json;
using Tracing.Sdk.Hash;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>Send several JSON records in one request via POST /api/anchors/batch.</summary>
internal static class BatchExample
{
    public static async Task RunAsync()
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = new SendOptions(DataType.Json, 5000), // dataType + request timeout in ms
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        var results = await sdk.SendBatchAsync(
            [
                new BatchRecord(JsonSerializer.Serialize(new { orderId = 1, amount = 10 }), Now()),
                new BatchRecord(JsonSerializer.Serialize(new { orderId = 2, amount = 20 }), Now()),
                new BatchRecord(JsonSerializer.Serialize(new { orderId = 3, amount = 30 }), Now()),
            ],
            SendOptions.ForTimeoutMs(30000)); // a batch gets more headroom than the 5s default

        foreach (var result in results)
        {
            Console.WriteLine($"[sent] {Keccak256Hasher.ToHex(result.Hash)} -> HTTP {result.Response.StatusCode}, {result.Response.RecordCount} record(s)");
        }
    }
}
