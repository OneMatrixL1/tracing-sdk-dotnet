using Tracing.Sdk.Hash;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>The batch flow with XML records.</summary>
internal static class XmlExample
{
    public static async Task RunAsync()
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = SendOptions.ForDataType(DataType.Xml), // default for every send/sendBatch
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        var results = await sdk.SendBatchAsync(
            [
                new BatchRecord(OrderXml(1, 10), Now()),
                new BatchRecord(OrderXml(2, 20), Now()),
                new BatchRecord(OrderXml(3, 30), Now()),
            ]);

        foreach (var result in results)
        {
            Console.WriteLine($"[sent] {Keccak256Hasher.ToHex(result.Hash)} -> HTTP {result.Response.StatusCode}, {result.Response.RecordCount} record(s)");
        }
    }

    private static string OrderXml(int orderId, int amount) =>
        $"<order><orderId>{orderId}</orderId><amount>{amount}</amount></order>";
}
