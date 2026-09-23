using System.Text.Json;
using Tracing.Sdk.Hash;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>Send a record, then look the anchor up again by its hash via GET /api/anchors?hash=...</summary>
internal static class QueryExample
{
    public static async Task RunAsync()
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = SendOptions.ForDataType(DataType.Json), // default for every send/sendBatch
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        var result = await sdk.SendAsync(JsonSerializer.Serialize(new { orderId = 1 }), Now());
        Console.WriteLine($"[sent] {Keccak256Hasher.ToHex(result.Hash)} -> HTTP {result.Response.StatusCode}");

        // Hashing is deterministic, so you can also query a hash you stored
        // earlier — or re-derived from the original record — without sending
        // again. A record can be anchored more than once, so the lookup
        // returns every proof, plus the proofType to pass to VerifyAsync.
        var anchor = await sdk.QueryByHashAsync(result.Hash);

        Console.WriteLine($"[found] {Keccak256Hasher.ToHex(anchor.Hash)} anchored by {anchor.Proof.Count} {anchor.ProofType} proof: {string.Join(", ", anchor.Proof.Select(Keccak256Hasher.ToHex))}");
    }
}
