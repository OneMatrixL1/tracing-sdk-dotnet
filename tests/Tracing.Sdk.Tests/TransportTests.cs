using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        else if (path.StartsWith("/refuse", StringComparison.Ordinal))
        {
            (status, answer) = (409, """{"error":"DRAW_NOT_LOCKED","message":"draw-1 has no lock"}""");
        }
        else if (path.StartsWith("/text", StringComparison.Ordinal))
        {
            answer = "plain text";
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
    private static readonly JsonObject Body = new() { ["draw"] = "draw-1" };

    private IndexerTransport Transport(string path = "", AuthConfig? auth = null, int timeoutMs = 10000) =>
        new(server.BaseUrl + path, auth ?? AuthConfig.ApiToken("token"), timeoutMs);

    [Fact]
    public async Task PostEventPostsToApiAnchorsWithTheEventType()
    {
        using var transport = Transport();

        var answer = await transport.PostEventAsync("lock", Body, null, default);
        var echo = answer.Json!.Value;

        Assert.Equal(200, answer.StatusCode);
        Assert.Equal("/api/anchors?eventType=lock", echo.GetProperty("path").GetString());
        Assert.Equal("POST", echo.GetProperty("method").GetString());
        Assert.StartsWith("application/json", echo.GetProperty("contentType").GetString());
        Assert.Equal("token", echo.GetProperty("apiKey").GetString());
        Assert.Equal("""{"draw":"draw-1"}""", echo.GetProperty("body").GetRawText());
    }

    [Fact]
    public async Task TheEventTypeIsUrlEncodedAndATrailingSlashTrimmed()
    {
        using var transport = Transport("/");

        var echo = (await transport.PostEventAsync("lock ts&x", Body, null, default)).Json!.Value;

        Assert.Equal("/api/anchors?eventType=lock%20ts%26x", echo.GetProperty("path").GetString());
    }

    [Fact]
    public async Task BasicAuthIsSent()
    {
        using var transport = Transport(auth: AuthConfig.Basic("user", "pa:ss"));

        var echo = (await transport.PostEventAsync("lock", Body, null, default)).Json!.Value;

        Assert.Equal("Basic " + Convert.ToBase64String("user:pa:ss"u8.ToArray()), echo.GetProperty("authorization").GetString());
    }

    [Fact]
    public async Task ANonJsonBodyIsReturnedAsText()
    {
        using var transport = Transport("/text");

        var answer = await transport.PostEventAsync("lock", Body, null, default);

        Assert.Equal("plain text", answer.Body);
        Assert.Null(answer.Json);
    }

    [Fact]
    public async Task ANon2xxStatusThrowsAnIndexerException()
    {
        using var transport = Transport("/fail");

        var e = await Assert.ThrowsAsync<IndexerException>(() => transport.PostEventAsync("lock", Body, null, default));

        Assert.Equal("Indexer returned HTTP 500: nope", e.Message);
        Assert.Equal(500, e.StatusCode);
        Assert.Null(e.ErrorCode);
    }

    [Fact]
    public async Task ARefusalCarriesTheErrorCode()
    {
        using var transport = Transport("/refuse");

        var e = await Assert.ThrowsAsync<IndexerException>(() => transport.PostEventAsync("lockts", Body, null, default));

        Assert.Equal((409, "DRAW_NOT_LOCKED", "draw-1 has no lock"), (e.StatusCode, e.ErrorCode, e.ServerMessage));
    }

    [Fact]
    public async Task ExceedingTheTimeoutThrows()
    {
        using var transport = Transport("/slow", timeoutMs: 5000);

        var e = await Assert.ThrowsAsync<TransportException>(() => transport.PostEventAsync("lock", Body, 50, default));
        Assert.Contains("timed out after 50 ms", e.Message);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNotATransportFailure()
    {
        using var transport = Transport("/slow");
        using var cancel = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.PostEventAsync("lock", Body, null, cancel.Token));
    }

    [Fact]
    public async Task AnUnreachableEndpointThrows()
    {
        using var transport = new IndexerTransport("http://127.0.0.1:1", AuthConfig.ApiToken("token"), 10000);

        var e = await Assert.ThrowsAsync<TransportException>(() => transport.PostEventAsync("lock", Body, null, default));
        Assert.IsNotType<IndexerException>(e);
    }
}
