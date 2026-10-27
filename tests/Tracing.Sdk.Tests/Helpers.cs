using System.Text.Json;
using System.Text.Json.Nodes;
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

    public override string ToString() => Id;

    private bool Flag(string name) => element.TryGetProperty(name, out var value) && value.GetBoolean();

    private string? String(string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;
}

internal sealed class FakeTransport : IIndexerTransport
{
    /// <summary>Timeout passed to each call, in call order.</summary>
    public List<int?> TimeoutCalls { get; } = [];

    /// <summary>Every event-type POST: its eventType and body as sent.</summary>
    public List<(string EventType, string Body)> EventCalls { get; } = [];

    /// <summary>The answer to an event-type POST; null answers like the tickets plugin.</summary>
    public Func<string, JsonObject, HttpAnswer>? EventResponse { get; set; }

    /// <summary>The drawRoot a lock or lockts answers with by default.</summary>
    public static readonly string DrawRoot = "0x" + string.Concat(Enumerable.Repeat("d7", 32));

    public bool Throw { get; set; }

    public Task<HttpAnswer> PostEventAsync(string eventType, JsonObject payload, int? timeoutMs, CancellationToken cancellationToken)
    {
        MaybeThrow();
        EventCalls.Add((eventType, payload.ToJsonString()));
        TimeoutCalls.Add(timeoutMs);

        return Task.FromResult(EventResponse?.Invoke(eventType, payload) ?? eventType switch
        {
            "batch" => Answer(200, """{"anchorId":"a-batch","batchId":42,"batchRoot":"0x01","ticketRoot":"0x02","txHash":"0x03"}"""),
            "lock" or "lockts" => Answer(202, $$"""{"blockchainStatus":"SUBMITTED","contractAddress":"0x5f","key":"{{DrawRoot}}","anchorId":"a-{{eventType}}"}"""),
            _ => Answer(202, $$"""{"blockchainStatus":"SUBMITTED","contractAddress":"0x5f","anchorId":"a-{{eventType}}"}"""),
        });
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
