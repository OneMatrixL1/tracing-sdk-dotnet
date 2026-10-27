using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tracing.Sdk.Events;

/// <summary>
/// What the Indexer answered to one record of an event type. The members
/// every type shares are properties; anything type-specific (<c>batchId</c>,
/// <c>batchRoot</c>, <c>txHash</c>, <c>blockchainStatus</c>, ...) is read with
/// <see cref="TryGetString"/> / <see cref="TryGetUInt64"/>, so a new schema
/// needs no new result class.
/// </summary>
public sealed class AnchorResult
{
    private readonly JsonObject _request;

    internal AnchorResult(string eventType, int statusCode, string body, JsonElement? json, JsonObject request)
    {
        EventType = eventType;
        StatusCode = statusCode;
        Body = body;
        Json = json;
        _request = request;
    }

    public string EventType { get; }

    /// <summary>200 for a Merkle type confirmed on chain, 202 for a submitted direct anchor.</summary>
    public int StatusCode { get; }

    /// <summary>The raw response body.</summary>
    public string Body { get; }

    /// <summary>The body parsed as JSON, or null when it is not JSON.</summary>
    public JsonElement? Json { get; }

    /// <summary>The request body exactly as sent, as JSON text.</summary>
    public string RequestBody => _request.ToJsonString();

    /// <summary>Names this anchor; its webhook carries the same value as <c>data.anchorId</c>.</summary>
    public string? AnchorId => TryGetString("anchorId", out var value) ? value : null;

    /// <summary>The key the backend derived (the tickets drawRoot for <c>lock</c> and <c>lockts</c>).</summary>
    public string? Key => TryGetString("key", out var value) ? value : null;

    /// <summary>A string member of the answer.</summary>
    public bool TryGetString(string name, out string value)
    {
        if (Json is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String)
        {
            value = member.GetString()!;

            return true;
        }

        value = "";

        return false;
    }

    /// <summary>An unsigned integer member of the answer.</summary>
    public bool TryGetUInt64(string name, out ulong value)
    {
        value = 0;

        return Json is { ValueKind: JsonValueKind.Object } body
            && body.TryGetProperty(name, out var member)
            && member.ValueKind == JsonValueKind.Number
            && member.TryGetUInt64(out value);
    }

    /// <summary>The normalized request, which link building reads.</summary>
    internal JsonObject Request => _request;
}
