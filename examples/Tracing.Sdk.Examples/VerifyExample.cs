using System.Text.Json;
using Tracing.Sdk.Hash;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>
/// Send a record, ask the Indexer which transactions anchored it, then check
/// that answer against the chain itself over your own RPC endpoint.
/// </summary>
internal static class VerifyExample
{
    public static async Task RunAsync()
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = new SendOptions(DataType.Json, 5000, RpcUrl), // dataType, timeoutMs, rpcUrl
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        var result = await sdk.SendAsync(JsonSerializer.Serialize(new { orderId = 1 }), Now());
        Console.WriteLine($"[sent] {Keccak256Hasher.ToHex(result.Hash)} -> HTTP {result.Response.StatusCode}");

        // What the Indexer claims: where this hash was anchored.
        var anchor = await sdk.QueryByHashAsync(result.Hash);

        // What the chain says. VerifyAsync reads the transaction receipt over
        // RPC and looks for an Anchored(bytes32,uint64) log carrying the
        // expected value, so a wrong or dishonest Indexer answer cannot pass.
        if (anchor.ProofType == TracingSdk.ModeMerkleProof)
        {
            // One proof: [transaction hash, sibling hashes...]. The siblings
            // fold the record hash into a Merkle root, which the transaction
            // must have anchored.
            Console.WriteLine($"[found] merkle proof in tx {Keccak256Hasher.ToHex(anchor.Proof[0])}, {anchor.Proof.Count - 1} sibling(s)");

            var verified = await sdk.VerifyAsync(anchor.Hash, anchor.Proof, anchor.ProofType);
            Console.WriteLine($"[verify] merkle root -> {(verified ? "VERIFIED" : "no matching anchor event")}");

            return;
        }

        // Transaction hashes: each proof is a transaction whose Anchored event
        // carries the record hash itself.
        Console.WriteLine($"[found] {anchor.Proof.Count} tx: {string.Join(", ", anchor.Proof.Select(Keccak256Hasher.ToHex))}");

        foreach (var proof in anchor.Proof)
        {
            var verified = await sdk.VerifyAsync(anchor.Hash, proof, anchor.ProofType);
            Console.WriteLine($"[verify] {Keccak256Hasher.ToHex(proof)} -> {(verified ? "VERIFIED" : "no matching anchor event")}");
        }
    }
}
