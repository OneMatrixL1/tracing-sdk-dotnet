using Tracing.Sdk.Events;

namespace Tracing.Sdk.Tests;

public class PortalLinkTests
{
    private const string Base = "https://integrity.vmatrix.com/v1/";

    private static readonly string Leaf1 = "0x" + new string('5', 64);

    private static readonly string Leaf2 = "0x" + new string('6', 64);

    private static (TracingSdk Sdk, FakeTransport Transport) MakeSdk(PortalConfig? portal = null)
    {
        var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = "https://indexer.example.com",
            Auth = AuthConfig.ApiToken("t"),
            Options = SendOptions.ForDataType(DataType.Json),
            EventTypes = EventTypeRegistry.Default().Register(EventPresets.Tickets),
            Portal = portal,
        });
        var transport = new FakeTransport();
        sdk.SetTransportForTesting(transport);

        return (sdk, transport);
    }

    [Fact]
    public async Task BatchGivesOneLinkPerTicketWithItsOwnNumber()
    {
        var (sdk, _) = MakeSdk();
        var result = await sdk.AnchorAsync(Tickets.Batch, new Dictionary<string, object?>
        {
            ["draw"] = "draw-1", ["channel"] = "web", ["sequence"] = 123UL, ["ticketHashes"] = new[] { Leaf1, Leaf2 }, ["value"] = 1UL,
        });

        var links = sdk.VerificationLinks(result, k => k == 0 ? "T-1" : null);

        Assert.Equal(
            [
                $"{Base}?type=batch&draw=draw-1&channel=web&sequence=123&ticketHash={Leaf1}#ticketId=VC0x",
                $"{Base}?type=batch&draw=draw-1&channel=web&sequence=124&ticketHash={Leaf2}",
            ],
            links);
    }

    [Fact]
    public async Task LockCarriesTheDerivedKey()
    {
        var (sdk, _) = MakeSdk();
        var result = await sdk.AnchorAsync(Tickets.Lock, new Dictionary<string, object?> { ["draw"] = "draw-1" });

        Assert.Equal($"{Base}?type=lock&draw=draw-1&key={FakeTransport.DrawRoot}", sdk.VerificationLink(result));
    }

    [Fact]
    public async Task LocktsLeavesTheTokenOut()
    {
        var (sdk, _) = MakeSdk();
        var result = await sdk.AnchorAsync(Tickets.LockTs, new Dictionary<string, object?> { ["draw"] = "draw-1", ["signature"] = new byte[] { 0x30, 0x82 } });

        Assert.Equal($"{Base}?type=lockts&draw=draw-1&key={FakeTransport.DrawRoot}", sdk.VerificationLink(result));
    }

    [Theory]
    [InlineData(Tickets.Events, true)]
    [InlineData(Tickets.Recon, true)]
    [InlineData(Tickets.Subagent, false)]
    public async Task RecordsCarryTheirHashAndDraw(string eventType, bool hasDraw)
    {
        var (sdk, _) = MakeSdk();
        var fields = new Dictionary<string, object?> { ["hash"] = Leaf1 };

        if (hasDraw)
        {
            fields["draw"] = "draw 1/ä";
        }

        var link = sdk.VerificationLink(await sdk.AnchorAsync(eventType, fields));
        var draw = hasDraw ? "&draw=draw%201%2F%C3%A4" : "";

        Assert.Equal($"{Base}?type={eventType}&hash={Leaf1}{draw}", link);
    }

    [Fact]
    public void ALinkFromKeptFields()
    {
        var (sdk, _) = MakeSdk();

        var ticket = sdk.VerificationLink(Tickets.Batch, new Dictionary<string, object?>
        {
            ["draw"] = "draw-1", ["channel"] = "web", ["sequence"] = 5, ["ticketHash"] = Leaf1.ToUpperInvariant().Replace("0X", "0x"),
        }, "T-1");
        var lockLink = sdk.VerificationLink(Tickets.Lock, new Dictionary<string, object?> { ["draw"] = "draw-1", ["key"] = FakeTransport.DrawRoot });

        Assert.Equal($"{Base}?type=batch&draw=draw-1&channel=web&sequence=5&ticketHash={Leaf1}#ticketId=VC0x", ticket);
        Assert.Equal($"{Base}?type=lock&draw=draw-1&key={FakeTransport.DrawRoot}", lockLink);
    }

    [Fact]
    public void KeptFieldsMustBeCompleteAndKnown()
    {
        var (sdk, _) = MakeSdk();

        Assert.Throws<ConfigException>(() => sdk.VerificationLink(Tickets.Batch, new Dictionary<string, object?> { ["draw"] = "d", ["channel"] = "c", ["sequence"] = 1 }));
        Assert.Throws<ConfigException>(() => sdk.VerificationLink(Tickets.Lock, new Dictionary<string, object?> { ["draw"] = "d" }));
        Assert.Throws<ConfigException>(() => sdk.VerificationLink(Tickets.Events, new Dictionary<string, object?> { ["hash"] = Leaf1, ["draw"] = "d", ["value"] = 1 }));
    }

    [Theory]
    [InlineData(Tickets.Lock)]
    [InlineData(Tickets.Events)]
    [InlineData(Tickets.Subagent)]
    public void ATicketIdIsOnlyForLeaves(string eventType)
    {
        var (sdk, _) = MakeSdk();
        var fields = new Dictionary<string, object?> { ["draw"] = "d", ["hash"] = Leaf1, ["key"] = FakeTransport.DrawRoot }
            .Where(f => EventPresets.Tickets.Single(t => t.Name == eventType).LinkFields.Any(l => l.Name == f.Key) || (eventType == Tickets.Lock && f.Key == "key"))
            .ToDictionary();

        Assert.NotNull(sdk.VerificationLink(eventType, fields));
        Assert.Throws<ConfigException>(() => sdk.VerificationLink(eventType, fields, "T-1"));
    }

    [Fact]
    public async Task ParamNamesAndBaseUrlAreConfigurable()
    {
        var (sdk, _) = MakeSdk(new PortalConfig
        {
            BaseUrl = "https://audit.example.org/check?v=2",
            ParamNames = new PortalParamNames("t", "id"),
        });

        var links = sdk.VerificationLinks(await sdk.AnchorAsync(Tickets.Batch, new Dictionary<string, object?>
        {
            ["draw"] = "d", ["channel"] = "c", ["sequence"] = 0, ["ticketHashes"] = new[] { Leaf1 }, ["value"] = 1,
        }), _ => "x");

        Assert.Equal([$"https://audit.example.org/check?v=2&t=batch&draw=d&channel=c&sequence=0&ticketHash={Leaf1}#id=eA"], links);
    }

    [Fact]
    public async Task AParamNameCollisionIsAConfigError()
    {
        var (sdk, _) = MakeSdk(new PortalConfig { ParamNames = new PortalParamNames(EventType: "draw") });
        var result = await sdk.AnchorAsync(Tickets.Lock, new Dictionary<string, object?> { ["draw"] = "d" });

        Assert.Throws<ConfigException>(() => sdk.VerificationLink(result));
    }

    [Fact]
    public async Task WithoutAPortalConfigLinksUseTheDefaultPortal()
    {
        var (sdk, _) = MakeSdk();
        var result = await sdk.AnchorAsync(Tickets.Subagent, new Dictionary<string, object?> { ["hash"] = Leaf1 });

        Assert.Equal($"{Base}?type=subagent&hash={Leaf1}", sdk.VerificationLink(result));
    }

    [Fact]
    public async Task MerkleAndDirectLinksAreNotMixedUp()
    {
        var (sdk, _) = MakeSdk();
        var batch = await sdk.AnchorAsync(Tickets.Batch, new Dictionary<string, object?>
        {
            ["draw"] = "d", ["channel"] = "c", ["sequence"] = 0, ["ticketHashes"] = new[] { Leaf1 }, ["value"] = 1,
        });
        var subagent = await sdk.AnchorAsync(Tickets.Subagent, new Dictionary<string, object?> { ["hash"] = Leaf1 });

        Assert.Throws<ConfigException>(() => sdk.VerificationLink(batch));
        Assert.Throws<ConfigException>(() => sdk.VerificationLinks(subagent));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://integrity.vmatrix.com/")]
    [InlineData("https://integrity.vmatrix.com/v1/#x")]
    public void ABadBaseUrlIsRefusedAtConstruction(string baseUrl)
    {
        Assert.Throws<ConfigException>(() => MakeSdk(new PortalConfig { BaseUrl = baseUrl }));
    }
}
