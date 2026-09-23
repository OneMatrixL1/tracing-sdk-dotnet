using System.Text.Json;
using Tracing.Sdk.Rpc;
using Tracing.Sdk.Transport;

namespace Tracing.Sdk.Tests;

/// <summary>
/// Loads the language-agnostic test vectors in testdata/*.json. They are kept
/// identical to the PHP and JS SDKs' testdata/, so every SDK is held to the
/// same canonicalizer/hasher outputs.
/// </summary>
public static class Fixtures
{
    public static IEnumerable<object[]> Cases(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", name);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => new object[] { new FixtureCase(c.Clone()) })
            .ToList();
    }
}

/// <summary>One fixture case; ToString() is its id, so test names read well.</summary>
public sealed class FixtureCase(JsonElement element)
{
    public string Id => element.GetProperty("id").GetString()!;

    public IReadOnlyList<string> Inputs => element.TryGetProperty("inputs", out var inputs)
        ? inputs.EnumerateArray().Select(i => i.GetString()!).ToList()
        : [element.GetProperty("input").GetString()!];

    public bool ExpectError => Flag("expectError");

    public bool AllowError => Flag("allowError");

    public string? ExpectedOutput => String("expectedOutput");

    public string? ExpectedHash => String("expectedHash");

    public string? ForbiddenSubstring => String("forbiddenSubstring");

    // merkle.json cases.
    public string Leaf => String("leaf")!;

    public IReadOnlyList<string> Proof => element.GetProperty("proof").EnumerateArray().Select(p => p.GetString()!).ToList();

    public string Root => String("root")!;

    public bool Valid => Flag("valid");

    public override string ToString() => Id;

    private bool Flag(string name) => element.TryGetProperty(name, out var value) && value.GetBoolean();

    private string? String(string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;
}

internal sealed class FakeTransport : IIndexerTransport
{
    /// <summary>The two transaction-hash proofs a query answers with by default.</summary>
    public static readonly string ProofA = "0x" + string.Concat(Enumerable.Repeat("ab", 32));

    public static readonly string ProofB = "0x" + string.Concat(Enumerable.Repeat("cd", 32));

    public List<AnchorEntry> SingleCalls { get; } = [];

    public List<IReadOnlyList<AnchorEntry>> BatchCalls { get; } = [];

    public List<string> QueryCalls { get; } = [];

    /// <summary>Timeout passed to each call, in call order.</summary>
    public List<int?> TimeoutCalls { get; } = [];

    public HttpAnswer? QueryResponse { get; set; }

    public bool Throw { get; set; }

    public Task<IndexerResponse> SendSingleAsync(AnchorEntry entry, int? timeoutMs, CancellationToken cancellationToken)
    {
        MaybeThrow();
        SingleCalls.Add(entry);
        TimeoutCalls.Add(timeoutMs);

        return Task.FromResult(new IndexerResponse(200, "", null, 1));
    }

    public Task<IndexerResponse> SendBatchAsync(IReadOnlyList<AnchorEntry> entries, int? timeoutMs, CancellationToken cancellationToken)
    {
        MaybeThrow();
        BatchCalls.Add(entries);
        TimeoutCalls.Add(timeoutMs);

        return Task.FromResult(new IndexerResponse(200, "", null, entries.Count));
    }

    public Task<HttpAnswer> QueryByHashAsync(string hash, int? timeoutMs, CancellationToken cancellationToken)
    {
        MaybeThrow();
        QueryCalls.Add(hash);
        TimeoutCalls.Add(timeoutMs);

        return Task.FromResult(QueryResponse ?? Answer(200, $$"""{"hash":"{{hash}}","proof":["{{ProofA}}","{{ProofB}}"],"proofType":"transactionHash"}"""));
    }

    public void Dispose()
    {
    }

    public static HttpAnswer Answer(int statusCode, string body)
    {
        JsonElement? json = null;

        try
        {
            json = JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException)
        {
        }

        return new HttpAnswer(statusCode, body, json);
    }

    private void MaybeThrow()
    {
        if (Throw)
        {
            throw new TransportException("boom");
        }
    }
}

internal sealed class FakeRpcTransport : IRpcTransport
{
    public List<(string RpcUrl, string TxHash, int? TimeoutMs)> Calls { get; } = [];

    /// <summary>The receipt JSON to answer with; null means "transaction unknown".</summary>
    public string? Receipt { get; set; }

    public bool Throw { get; set; }

    public Task<JsonElement?> GetTransactionReceiptAsync(string rpcUrl, string txHash, int? timeoutMs, CancellationToken cancellationToken)
    {
        if (Throw)
        {
            throw new TransportException("boom");
        }

        Calls.Add((rpcUrl, txHash, timeoutMs));

        return Task.FromResult(Receipt is null ? (JsonElement?)null : JsonDocument.Parse(Receipt).RootElement.Clone());
    }

    public void Dispose()
    {
    }
}
