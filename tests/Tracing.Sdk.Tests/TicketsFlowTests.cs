using System.Text.Json;
using System.Text.Json.Nodes;
using Tracing.Sdk.Events;
using Tracing.Sdk.Transport;

namespace Tracing.Sdk.Tests;

/// <summary>The life of a draw from docs/TICKETS-INTEGRATION.md, against a fake Indexer.</summary>
public class TicketsFlowTests
{
    private static readonly string Leaf1 = "0x" + new string('5', 64);

    private static readonly string Leaf2 = "0x" + new string('6', 64);

    private static (TracingSdk Sdk, FakeTransport Transport) MakeSdk()
    {
        var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = "https://indexer.example.com",
            Auth = AuthConfig.ApiToken("t"),
            Options = SendOptions.ForDataType(DataType.Json),
            EventTypes = EventTypeRegistry.Default().Register(EventPresets.Tickets),
        });
        var transport = new FakeTransport();
        sdk.SetTransportForTesting(transport);

        return (sdk, transport);
    }

    [Fact]
    public async Task ADrawFromBatchToRecords()
    {
        var (sdk, transport) = MakeSdk();

        var batch = await sdk.AnchorAsync(Tickets.Batch, new Dictionary<string, object?>
        {
            ["draw"] = "draw-1", ["channel"] = "channel-1", ["sequence"] = 0, ["ticketHashes"] = new[] { Leaf1, Leaf2 }, ["value"] = 1,
        });
        var locked = await sdk.AnchorAsync(Tickets.Lock, new Dictionary<string, object?> { ["draw"] = "draw-1" });
        var stamped = await sdk.AnchorAsync(Tickets.LockTs, new Dictionary<string, object?> { ["draw"] = "draw-1", ["signature"] = "0x3082" });
        await sdk.AnchorAsync(Tickets.Events, new Dictionary<string, object?> { ["hash"] = Leaf1, ["draw"] = "draw-1" });
        await sdk.AnchorAsync(Tickets.Recon, new Dictionary<string, object?> { ["hash"] = Leaf2, ["draw"] = "draw-1" });
        await sdk.AnchorAsync(Tickets.Subagent, new Dictionary<string, object?> { ["hash"] = Leaf1 });

        Assert.Equal(
            [
                ("batch", $$"""{"draw":"draw-1","channel":"channel-1","sequence":0,"ticketHashes":["{{Leaf1}}","{{Leaf2}}"],"value":1}"""),
                ("lock", """{"draw":"draw-1"}"""),
                ("lockts", """{"draw":"draw-1","signature":"0x3082"}"""),
                ("events", $$"""{"hash":"{{Leaf1}}","draw":"draw-1"}"""),
                ("recon", $$"""{"hash":"{{Leaf2}}","draw":"draw-1"}"""),
                ("subagent", $$"""{"hash":"{{Leaf1}}"}"""),
            ],
            transport.EventCalls);

        Assert.Equal(200, batch.StatusCode);
        Assert.Equal("a-batch", batch.AnchorId);
        Assert.True(batch.TryGetUInt64("batchId", out var batchId));
        Assert.Equal(42UL, batchId);
        Assert.True(batch.TryGetString("batchRoot", out var batchRoot));
        Assert.Equal("0x01", batchRoot);
        Assert.Null(batch.Key);

        Assert.Equal(202, locked.StatusCode);
        Assert.Equal(FakeTransport.DrawRoot, locked.Key);
        Assert.Equal("a-lockts", stamped.AnchorId);
    }

    [Fact]
    public async Task AJsonObjectWorksAsFields()
    {
        var (sdk, transport) = MakeSdk();

        await sdk.AnchorAsync(Tickets.Batch, JsonNode.Parse($$"""
            {"draw":"d","channel":"c","sequence":18446744073709551615,"ticketHashes":["{{Leaf1}}"],"value":0}
            """)!.AsObject());

        Assert.Equal(
            $$"""{"draw":"d","channel":"c","sequence":18446744073709551615,"ticketHashes":["{{Leaf1}}"],"value":0}""",
            Assert.Single(transport.EventCalls).Body);
    }

    [Fact]
    public async Task ABadRequestIsNeverSent()
    {
        var (sdk, transport) = MakeSdk();

        await Assert.ThrowsAsync<ConfigException>(() => sdk.AnchorAsync(Tickets.Lock, new Dictionary<string, object?> { ["draw"] = "d", ["drawRoot"] = Leaf1 }));
        await Assert.ThrowsAsync<ConfigException>(() => sdk.AnchorAsync("unknown", new Dictionary<string, object?>()));
        Assert.Empty(transport.EventCalls);
    }

    [Fact]
    public async Task ThePerCallTimeoutReachesTheTransport()
    {
        var (sdk, transport) = MakeSdk();

        await sdk.AnchorAsync(Tickets.Subagent, new Dictionary<string, object?> { ["hash"] = Leaf1 }, SendOptions.ForTimeoutMs(90_000));

        Assert.Equal([90_000], transport.TimeoutCalls);
    }

    [Fact]
    public void AnIndexerRefusalCarriesItsErrorFields()
    {
        const string body = """{"error":"DUPLICATE_HASH","field":"ticketHashes","index":2,"message":"repeated","batchId":7}""";
        var e = new IndexerException("Indexer returned HTTP 409: " + body, 409, body, JsonDocument.Parse(body).RootElement);

        Assert.IsAssignableFrom<TransportException>(e);
        Assert.Equal((409, "DUPLICATE_HASH", "ticketHashes", 2, "repeated", 7L), (e.StatusCode, e.ErrorCode, e.Field, e.Index, e.ServerMessage, e.BatchId));
    }

    [Fact]
    public async Task AnIndexerRefusalIsThrownAsIs()
    {
        var (sdk, transport) = MakeSdk();
        transport.EventResponse = (_, _) => throw new IndexerException("409", 409, """{"error":"DRAW_NOT_LOCKED"}""", null);

        var e = await Assert.ThrowsAsync<IndexerException>(() => sdk.AnchorAsync(Tickets.LockTs, new Dictionary<string, object?> { ["draw"] = "d", ["signature"] = "0x30" }));

        Assert.Equal(409, e.StatusCode);
    }
}

public class EventTransportTests(EchoServer server) : IClassFixture<EchoServer>
{
    [Fact]
    public async Task PostEventPutsTheEventTypeInTheQuery()
    {
        using var transport = new IndexerTransport(server.BaseUrl, AuthConfig.ApiToken("token"), 10000);

        var answer = await transport.PostEventAsync("lock ts", new JsonObject { ["draw"] = "d" }, null, default);
        var echo = answer.Json!.Value;

        Assert.Equal("/api/anchors?eventType=lock%20ts", echo.GetProperty("path").GetString());
        Assert.Equal("POST", echo.GetProperty("method").GetString());
        Assert.Equal("""{"draw":"d"}""", echo.GetProperty("body").GetRawText());
    }

    [Fact]
    public async Task ANon2xxAnswerIsAnIndexerException()
    {
        using var transport = new IndexerTransport(server.BaseUrl + "/fail", AuthConfig.ApiToken("token"), 10000);

        var e = await Assert.ThrowsAsync<IndexerException>(() => transport.PostEventAsync("lock", new JsonObject(), null, default));

        Assert.Equal(500, e.StatusCode);
        Assert.Null(e.ErrorCode);
        Assert.Equal("Indexer returned HTTP 500: nope", e.Message);
    }
}
