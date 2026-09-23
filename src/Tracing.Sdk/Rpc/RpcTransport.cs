using System.Text.Json;
using System.Text.Json.Nodes;
using Tracing.Sdk.Transport;

namespace Tracing.Sdk.Rpc;

/// <summary>JSON-RPC access to a chain node; swappable in tests.</summary>
internal interface IRpcTransport : IDisposable
{
    /// <summary>
    /// Fetch a transaction receipt via eth_getTransactionReceipt.
    /// </summary>
    /// <returns>the receipt, or null when the node does not know the transaction</returns>
    /// <exception cref="TransportException">the request fails or the node answers with a JSON-RPC error</exception>
    Task<JsonElement?> GetTransactionReceiptAsync(string rpcUrl, string txHash, int? timeoutMs, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal JSON-RPC 2.0 client, used to read anchor events straight from a
/// chain node. Deliberately separate from the Indexer transport: the RPC
/// endpoint is a different service and the Indexer's auth must not leak to it.
/// </summary>
internal sealed class RpcTransport(int timeoutMs) : IRpcTransport
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<JsonElement?> GetTransactionReceiptAsync(string rpcUrl, string txHash, int? callTimeoutMs, CancellationToken cancellationToken)
    {
        var result = await CallAsync(rpcUrl, "eth_getTransactionReceipt", new JsonArray(txHash), callTimeoutMs, cancellationToken).ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new TransportException("Unexpected eth_getTransactionReceipt result, expected an object");
        }

        return result;
    }

    public void Dispose() => _client.Dispose();

    private async Task<JsonElement> CallAsync(string rpcUrl, string method, JsonArray parameters, int? callTimeoutMs, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = parameters,
        };

        var answer = await HttpRequester.SendAsync(
            _client, HttpMethod.Post, rpcUrl, payload.ToJsonString(), callTimeoutMs ?? timeoutMs,
            $"JSON-RPC request to {rpcUrl}", cancellationToken).ConfigureAwait(false);

        if (answer.StatusCode is < 200 or >= 300)
        {
            throw new TransportException($"RPC endpoint returned HTTP {answer.StatusCode}: {answer.Body}");
        }

        if (answer.Json is not { ValueKind: JsonValueKind.Object } response)
        {
            throw new TransportException("RPC endpoint returned a non-JSON body: " + answer.Body);
        }

        if (response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                ? (m.ValueKind == JsonValueKind.String ? m.GetString() : m.ToString())
                : error.GetRawText();

            throw new TransportException($"JSON-RPC error from {method}: {message}");
        }

        if (!response.TryGetProperty("result", out var result))
        {
            throw new TransportException($"JSON-RPC response for {method} has no \"result\" member");
        }

        return result;
    }
}
