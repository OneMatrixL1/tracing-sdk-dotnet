using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tracing.Sdk.Rpc;
using Tracing.Sdk.Transport;

namespace Tracing.Sdk.Tests;

/// <summary>
/// A local server that echoes back the request it received, so the tests can
/// assert on the real URL path, method, headers and body the transports send.
/// A few paths answer differently to exercise the error handling.
/// </summary>
public sealed class EchoServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public EchoServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public void Dispose() => _listener.Close();

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private static async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var body = await new StreamReader(request.InputStream).ReadToEndAsync();
        var path = request.RawUrl!;
        var (status, answer) = (200, "");

        if (path.StartsWith("/slow", StringComparison.Ordinal))
        {
            await Task.Delay(1000);
            answer = "{}";
        }
        else if (path.StartsWith("/fail", StringComparison.Ordinal))
        {
            (status, answer) = (500, "nope");
        }
        else if (path.StartsWith("/text", StringComparison.Ordinal))
        {
            answer = "plain text";
        }
        else if (path.StartsWith("/rpc", StringComparison.Ordinal))
        {
            var call = JsonDocument.Parse(body).RootElement;
            answer = path switch
            {
                "/rpc/receipt" => $$$"""{"jsonrpc":"2.0","id":1,"result":{"logs":[],"echo":{{{call.GetRawText()}}}}}""",
                "/rpc/unknown" => """{"jsonrpc":"2.0","id":1,"result":null}""",
                "/rpc/error" => """{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"bad tx"}}""",
                _ => """{"jsonrpc":"2.0","id":1}""",
            };
        }
        else
        {
            answer = JsonSerializer.Serialize(new
            {
                path,
                method = request.HttpMethod,
                contentType = request.ContentType,
                apiKey = request.Headers["X-API-Key"],
                authorization = request.Headers["Authorization"],
                body = body.Length == 0 ? (JsonElement?)null : JsonDocument.Parse(body).RootElement,
            });
        }

        var bytes = Encoding.UTF8.GetBytes(answer);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        try
        {
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
        {
            // The client gave up (the timeout test); nothing to answer.
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();

        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}

public class TransportTests(EchoServer server) : IClassFixture<EchoServer>
{
    private static readonly AnchorEntry Entry = new("0xabc", 1);

    private IndexerTransport Transport(string path = "", AuthConfig? auth = null, int timeoutMs = 10000) =>
        new(server.BaseUrl + path, auth ?? AuthConfig.ApiToken("token"), timeoutMs);

    [Fact]
    public async Task SendSinglePostsToApiAnchors()
    {
        using var transport = Transport();

        var result = await transport.SendSingleAsync(Entry, null, default);
        var echo = result.Json!.Value;

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, result.RecordCount);
        Assert.Equal("/api/anchors", echo.GetProperty("path").GetString());
        Assert.Equal("POST", echo.GetProperty("method").GetString());
        Assert.StartsWith("application/json", echo.GetProperty("contentType").GetString());
        Assert.Equal("token", echo.GetProperty("apiKey").GetString());
        Assert.Equal("""{"hash":"0xabc","signingTime":1}""", echo.GetProperty("body").GetRawText());
    }

    [Fact]
    public async Task SendBatchPostsToApiAnchorsBatch()
    {
        using var transport = Transport("/");

        var result = await transport.SendBatchAsync([Entry, new AnchorEntry("0xdef", "2026-09-23T00:00:00Z")], null, default);
        var echo = result.Json!.Value;

        Assert.Equal(2, result.RecordCount);
        Assert.Equal("/api/anchors/batch", echo.GetProperty("path").GetString());
        Assert.Equal("""[{"hash":"0xabc","signingTime":1},{"hash":"0xdef","signingTime":"2026-09-23T00:00:00Z"}]""", echo.GetProperty("body").GetRawText());
    }

    [Fact]
    public async Task QueryByHashGetsApiAnchorsWithTheHashUrlEncoded()
    {
        using var transport = Transport(auth: AuthConfig.Basic("user", "pa:ss"));

        var echo = (await transport.QueryByHashAsync("0xab&c", null, default)).Json!.Value;

        Assert.Equal("/api/anchors?hash=0xab%26c", echo.GetProperty("path").GetString());
        Assert.Equal("GET", echo.GetProperty("method").GetString());
        Assert.Equal("Basic " + Convert.ToBase64String("user:pa:ss"u8.ToArray()), echo.GetProperty("authorization").GetString());
    }

    [Fact]
    public async Task ANonJsonBodyIsReturnedAsText()
    {
        using var transport = Transport("/text");

        var result = await transport.SendSingleAsync(Entry, null, default);

        Assert.Equal("plain text", result.Body);
        Assert.Null(result.Json);
    }

    [Fact]
    public async Task ANon2xxStatusThrows()
    {
        using var transport = Transport("/fail");

        var e = await Assert.ThrowsAsync<TransportException>(() => transport.SendSingleAsync(Entry, null, default));
        Assert.Equal("Indexer returned HTTP 500: nope", e.Message);
    }

    [Fact]
    public async Task ExceedingTheTimeoutThrows()
    {
        using var transport = Transport("/slow", timeoutMs: 5000);

        var e = await Assert.ThrowsAsync<TransportException>(() => transport.SendSingleAsync(Entry, 50, default));
        Assert.Contains("timed out after 50 ms", e.Message);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNotATransportFailure()
    {
        using var transport = Transport("/slow");
        using var cancel = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendSingleAsync(Entry, null, cancel.Token));
    }

    [Fact]
    public async Task AnUnreachableEndpointThrows()
    {
        using var transport = new IndexerTransport("http://127.0.0.1:1", AuthConfig.ApiToken("token"), 10000);

        await Assert.ThrowsAsync<TransportException>(() => transport.SendSingleAsync(Entry, null, default));
    }

    [Fact]
    public async Task RpcCallsEthGetTransactionReceipt()
    {
        using var rpc = new RpcTransport(10000);

        var receipt = await rpc.GetTransactionReceiptAsync(server.BaseUrl + "/rpc/receipt", "0xabc", null, default);

        Assert.Equal(
            """{"jsonrpc":"2.0","id":1,"method":"eth_getTransactionReceipt","params":["0xabc"]}""",
            receipt!.Value.GetProperty("echo").GetRawText());
    }

    [Fact]
    public async Task RpcReturnsNullForAnUnknownTransaction()
    {
        using var rpc = new RpcTransport(10000);

        Assert.Null(await rpc.GetTransactionReceiptAsync(server.BaseUrl + "/rpc/unknown", "0xabc", null, default));
    }

    [Theory]
    [InlineData("/rpc/error", "JSON-RPC error from eth_getTransactionReceipt: bad tx")]
    [InlineData("/rpc/noresult", "has no \"result\" member")]
    [InlineData("/text", "non-JSON body")]
    public async Task RpcFailuresThrow(string path, string message)
    {
        using var rpc = new RpcTransport(10000);

        var e = await Assert.ThrowsAsync<TransportException>(() => rpc.GetTransactionReceiptAsync(server.BaseUrl + path, "0xabc", null, default));
        Assert.Contains(message, e.Message);
    }
}
