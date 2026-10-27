using System.Text.Json;
using Tracing.Sdk.Events;

namespace Tracing.Sdk.Tests;

public class EventDefinitionTests
{
    private static string Testdata(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", name));

    /// <summary>Records hold lists, which compare by reference; their JSON compares by value.</summary>
    private static string Describe(EventTypeDefinition definition) => JsonSerializer.Serialize(definition);

    [Fact]
    public void TicketsJsonMatchesTheTicketsPreset()
    {
        var fromJson = EventTypeRegistry.ParseJson(Testdata("tickets-events.json"));

        Assert.Equal(EventPresets.Tickets.Select(Describe), fromJson.Select(Describe));
    }

    [Fact]
    public void ExampleConfigIsTheTestdataFile()
    {
        var example = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "Tracing.Sdk.Examples", "config", "tickets-events.json");

        Assert.Equal(Testdata("tickets-events.json"), File.ReadAllText(example));
    }

    [Fact]
    public void PresetsRegister()
    {
        var registry = EventTypeRegistry.Default().Register(EventPresets.Tickets);

        Assert.Equal(["anchor", "batch", "events", "lock", "lockts", "recon", "subagent"], registry.Names.Order());
    }

    [Fact]
    public void PluginMembersTheSdkDoesNotUseAreIgnored()
    {
        var types = EventTypeRegistry.ParseJson("""
            [{
              "name": "subagent",
              "anchoring": { "mode": "transactionHash" },
              "request": { "fields": [ { "name": "hash", "type": "bytes32", "required": true, "role": "key" } ] },
              "response": { "accepted": ["blockchainStatus"] },
              "chain": { "event": { "signature": "SubagentAnchored(bytes32)" } }
            }]
            """);

        Assert.Equal(Describe(EventPresets.Tickets[^1]), Describe(Assert.Single(types)));
    }

    [Fact]
    public void DefaultLinkFieldsAreTheRequiredScalarFields()
    {
        var lockts = EventPresets.Tickets.Single(t => t.Name == Tickets.LockTs);
        var events = EventPresets.Tickets.Single(t => t.Name == Tickets.Events);
        var batch = EventPresets.Tickets.Single(t => t.Name == Tickets.Batch);

        // The DER token is bytes: never in a link.
        Assert.Equal(["draw"], lockts.LinkFields.Select(f => f.Name));
        Assert.Equal(["hash", "draw"], events.LinkFields.Select(f => f.Name));
        Assert.Equal(["draw", "channel", "sequence"], batch.LinkFields.Select(f => f.Name));
    }

    public static TheoryData<string, string> InvalidDefinitions => new()
    {
        { "no leaves", """{"name":"t","anchoring":{"mode":"merkle"},"request":{"fields":[{"name":"a","type":"string"}]}}""" },
        { "leaves on a direct type", """{"name":"t","request":{"fields":[{"name":"l","type":"bytes32[]","role":"leaves"}]}}""" },
        { "key of the wrong type", """{"name":"t","request":{"fields":[{"name":"k","type":"string","role":"key"}]}}""" },
        { "two keys", """{"name":"t","request":{"fields":[{"name":"a","type":"bytes32","role":"key"},{"name":"b","type":"bytes32","role":"key"}]}}""" },
        { "a field twice", """{"name":"t","request":{"fields":[{"name":"a","type":"string"},{"name":"a","type":"string"}]}}""" },
        { "derived key and key field", """{"name":"t","anchoring":{"key":"derived"},"request":{"fields":[{"name":"a","type":"bytes32","role":"key"}]}}""" },
        { "reserved leaf prefix", """{"name":"t","anchoring":{"mode":"merkle"},"request":{"fields":[{"name":"l","type":"bytes32[]","role":"leaves"}]},"hashing":{"leafPrefix":"0x01"}}""" },
        { "unknown link field", """{"name":"t","request":{"fields":[{"name":"a","type":"string"}]},"link":{"fields":["b"]}}""" },
        { "bytes link field", """{"name":"t","request":{"fields":[{"name":"a","type":"bytes"}]},"link":{"fields":["a"]}}""" },
        { "unknown type", """{"name":"t","request":{"fields":[{"name":"a","type":"uint256"}]}}""" },
        { "unknown mode", """{"name":"t","anchoring":{"mode":"tree"},"request":{"fields":[]}}""" },
        { "no fields", """{"name":"t"}""" },
        { "no name", """{"request":{"fields":[]}}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public void InconsistentDefinitionsAreRefused(string _, string json)
    {
        Assert.Throws<ConfigException>(() => EventTypeRegistry.FromJson($"[{json}]"));
    }

    [Fact]
    public void ANameRegisteredTwiceIsRefused()
    {
        Assert.Throws<ConfigException>(() => EventTypeRegistry.Default().Register(EventPresets.Anchor));
    }

    [Fact]
    public void AnUnknownNameIsAConfigError()
    {
        var e = Assert.Throws<ConfigException>(() => EventTypeRegistry.Default().Get("batch"));

        Assert.Contains("anchor", e.Message);
    }

    [Fact]
    public void TheExampleEventFilesLoad()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "Tracing.Sdk.Examples", "config");

        Assert.Equal(6, EventTypeRegistry.FromJsonFile(Path.Combine(dir, "tickets-events.json")).Names.Count);
        Assert.Equal(["shipment"], EventTypeRegistry.FromJsonFile(Path.Combine(dir, "custom-events.example.json")).Names);
    }

    [Fact]
    public void AMissingEventFileIsAConfigError()
    {
        Assert.Throws<ConfigException>(() => EventTypeRegistry.FromJsonFile("/nonexistent/events.json"));
    }
}
