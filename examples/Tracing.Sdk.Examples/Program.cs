// Runnable examples. Each one points at http://localhost:3000 with a
// placeholder token — replace the endpoint, token (and rpcUrl for "verify")
// with the values you were given, then run:
//
//   dotnet run --project examples/Tracing.Sdk.Examples -- <example>
//
// where <example> is one of: single, batch, xml, query, verify.
using System.Text.Json;
using Tracing.Sdk;

const string Endpoint = "http://localhost:3000"; // replace with your provided indexer endpoint
const string ApiToken = "your-api-token"; // replace with your provided API token
const string RpcUrl = "http://localhost:8545"; // replace with your chain node (verify only)

static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

var examples = new Dictionary<string, Func<Task>>
{
    // Send a single record right away via POST /api/anchors instead of batching.
    ["single"] = async () =>
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = SendOptions.ForDataType(DataType.Raw), // default for every send/sendBatch
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        // Override the default data type for this send.
        var result = await sdk.SendAsync(JsonSerializer.Serialize(new { orderId = 1 }), Now(), SendOptions.ForDataType(DataType.Json));
        Console.WriteLine($"[sent] {result.Hash} -> HTTP {result.Response.StatusCode} {result.Response.Body}");
    },

    // Several JSON records in one request via POST /api/anchors/batch.
    ["batch"] = async () =>
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
            Console.WriteLine($"[sent] {result.Hash} -> HTTP {result.Response.StatusCode}, {result.Response.RecordCount} record(s)");
        }
    },

    // The batch flow with XML records.
    ["xml"] = async () =>
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = SendOptions.ForDataType(DataType.Xml),
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        static string OrderXml(int orderId, int amount) => $"<order><orderId>{orderId}</orderId><amount>{amount}</amount></order>";

        var results = await sdk.SendBatchAsync(
            [new BatchRecord(OrderXml(1, 10), Now()), new BatchRecord(OrderXml(2, 20), Now()), new BatchRecord(OrderXml(3, 30), Now())]);

        foreach (var result in results)
        {
            Console.WriteLine($"[sent] {result.Hash} -> HTTP {result.Response.StatusCode}, {result.Response.RecordCount} record(s)");
        }
    },

    // Send a record, then look the anchor up again by its hash.
    ["query"] = async () =>
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = SendOptions.ForDataType(DataType.Json),
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        var result = await sdk.SendAsync(JsonSerializer.Serialize(new { orderId = 1 }), Now());
        Console.WriteLine($"[sent] {result.Hash} -> HTTP {result.Response.StatusCode}");

        // Hashing is deterministic, so you can also query a hash you stored
        // earlier — or re-derived from the original record — without sending
        // again. A record can be anchored more than once, so the lookup
        // returns every proof, plus the proofType to pass to VerifyAsync.
        var anchor = await sdk.QueryByHashAsync(result.Hash);
        Console.WriteLine($"[found] {anchor.Hash} anchored by {anchor.Proof.Count} {anchor.ProofType} proof: {string.Join(", ", anchor.Proof)}");
    },

    // Ask the Indexer which transactions anchored a record, then check that
    // answer against the chain itself over your own RPC endpoint.
    ["verify"] = async () =>
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = Endpoint,
            Options = new SendOptions(DataType.Json, 5000, RpcUrl),
            Auth = AuthConfig.ApiToken(ApiToken),
        });

        var result = await sdk.SendAsync(JsonSerializer.Serialize(new { orderId = 1 }), Now());
        Console.WriteLine($"[sent] {result.Hash} -> HTTP {result.Response.StatusCode}");

        var anchor = await sdk.QueryByHashAsync(result.Hash);
        Console.WriteLine($"[found] {anchor.Proof.Count} tx: {string.Join(", ", anchor.Proof)}");

        // VerifyAsync reads the transaction receipt over RPC and looks for an
        // Anchored(bytes32,uint64) log carrying this exact hash, so a wrong or
        // dishonest Indexer answer cannot pass.
        foreach (var proof in anchor.Proof)
        {
            var verified = await sdk.VerifyAsync(anchor.Hash, proof, anchor.ProofType);
            Console.WriteLine($"[verify] {proof} -> {(verified ? "VERIFIED" : "no matching anchor event")}");
        }
    },
};

if (args.Length != 1 || !examples.TryGetValue(args[0], out var example))
{
    Console.Error.WriteLine($"usage: dotnet run -- <{string.Join("|", examples.Keys)}>");
    return 2;
}

try
{
    await example();
    return 0;
}
catch (TracingSdkException e)
{
    // A TransportException from "verify" can also mean the transaction is not
    // mined yet, or the RPC node does not have it — retry rather than conclude.
    Console.Error.WriteLine($"[error] {e.GetType().Name}: {e.Message}");
    return 1;
}
