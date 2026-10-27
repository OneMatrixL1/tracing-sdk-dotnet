using Tracing.Sdk.Events;
using Tracing.Sdk.Hash;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>
/// Reuse the SDK for a schema it does not ship: config/custom-events.example.json
/// declares a "shipment" type (its fields, how it is hashed, what its link
/// carries), and the same calls as for tickets anchor it.
/// </summary>
internal static class CustomEventExample
{
    public static async Task RunAsync()
    {
        using var sdk = CreateSdk("custom-events.example.json");

        const string shipment = """
            <shipment id="SH-1001">
              <warehouse>HN-01</warehouse>
              <weight unit="g">12500</weight>
            </shipment>
            """;

        // The type's hashing says XML, so this is Exclusive C14N, then keccak256.
        var hash = sdk.HashFor("shipment", shipment);

        AnchorResult result;

        try
        {
            result = await sdk.AnchorAsync("shipment", new Dictionary<string, object?>
            {
                ["hash"] = hash,
                ["shipmentId"] = "SH-1001",
                ["warehouse"] = "HN-01",
                ["weightGrams"] = 12_500UL,
            });
        }
        catch (IndexerException e) when (e.ErrorCode == "UNKNOWN_EVENT_TYPE")
        {
            // The SDK side is only config; the backend still needs a plugin declaring the same type.
            Console.WriteLine($"[shipment] {Keccak256Hasher.ToHex(hash)} validated and sent, but this Indexer has no \"shipment\" plugin");
            return;
        }

        Console.WriteLine($"[shipment] {Keccak256Hasher.ToHex(hash)} -> HTTP {result.StatusCode}, anchorId {result.AnchorId}");
        Console.WriteLine($"  [link] {sdk.VerificationLink(result)}");
    }
}
