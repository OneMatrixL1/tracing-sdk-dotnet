using System.Numerics;
using System.Text.Json.Nodes;
using Tracing.Sdk.Events;

namespace Tracing.Sdk.Tests;

public class FieldValidatorTests
{
    private static readonly EventTypeDefinition Batch = new EventTypeDefinition
    {
        Name = "batch",
        Anchoring = new(AnchoringMode.Merkle, MaxTreeSize: 3),
        RequestFields = EventPresets.Tickets[0].RequestFields,
    };

    private static readonly EventTypeDefinition LockTs = EventPresets.Tickets.Single(t => t.Name == Tickets.LockTs);

    private static readonly EventTypeDefinition Events = EventPresets.Tickets.Single(t => t.Name == Tickets.Events);

    private static readonly string Leaf1 = "0x" + new string('5', 64);

    private static readonly string Leaf2 = "0x" + new string('6', 64);

    private static Dictionary<string, object?> BatchFields(object? leaves = null, object? sequence = null) => new()
    {
        ["draw"] = "draw-1",
        ["channel"] = "channel-1",
        ["sequence"] = sequence ?? 0,
        ["ticketHashes"] = leaves ?? new[] { Leaf1, Leaf2 },
        ["value"] = 1UL,
    };

    private static JsonObject Build(EventTypeDefinition definition, Dictionary<string, object?> fields) => FieldValidator.BuildRequest(definition, fields);

    [Fact]
    public void BuildsTheRequestInDeclarationOrder()
    {
        var body = Build(Batch, BatchFields());

        Assert.Equal(
            $$"""{"draw":"draw-1","channel":"channel-1","sequence":0,"ticketHashes":["{{Leaf1}}","{{Leaf2}}"],"value":1}""",
            body.ToJsonString());
    }

    [Fact]
    public void Bytes32IsNormalizedToLowercase0xHex()
    {
        var body = Build(Events, new() { ["hash"] = "AB" + new string('c', 62), ["draw"] = "d" });

        Assert.Equal("0xab" + new string('c', 62), body["hash"]!.GetValue<string>());
    }

    [Fact]
    public void Bytes32AcceptsBytes()
    {
        var hash = Enumerable.Repeat((byte)0x11, 32).ToArray();

        Assert.Equal("0x" + new string('1', 64), Build(Events, new() { ["hash"] = hash, ["draw"] = "d" })["hash"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0x1234")]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0xzz11111111111111111111111111111111111111111111111111111111111111")]
    public void BadBytes32IsRefused(string hash)
    {
        Assert.Throws<ConfigException>(() => Build(Events, new() { ["hash"] = hash, ["draw"] = "d" }));
    }

    [Fact]
    public void Uint64AboveTwoToThe53IsKeptExact()
    {
        var body = Build(Batch, BatchFields(sequence: ulong.MaxValue));

        Assert.Contains("\"sequence\":18446744073709551615", body.ToJsonString());
    }

    [Fact]
    public void Uint64AcceptsBigIntegerAndJsonNumbers()
    {
        Assert.Equal(7UL, Build(Batch, BatchFields(sequence: new BigInteger(7)))["sequence"]!.GetValue<ulong>());
        Assert.Equal(9UL, Build(Batch, BatchFields(sequence: JsonNode.Parse("9")))["sequence"]!.GetValue<ulong>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData("12")]
    [InlineData(1.5)]
    public void BadUint64IsRefused(object sequence)
    {
        Assert.Throws<ConfigException>(() => Build(Batch, BatchFields(sequence: sequence)));
    }

    [Fact]
    public void ARepeatedLeafNamesBothPositions()
    {
        var e = Assert.Throws<ConfigException>(() => Build(Batch, BatchFields(new[] { Leaf1, Leaf2, Leaf1.ToUpperInvariant().Replace("0X", "0x") })));

        Assert.Contains("\"ticketHashes[2]\" repeats index 0", e.Message);
    }

    [Fact]
    public void LeavesAreBetweenOneAndMaxTreeSize()
    {
        Assert.Throws<ConfigException>(() => Build(Batch, BatchFields(Array.Empty<string>())));

        var four = Enumerable.Range(1, 4).Select(i => "0x" + i.ToString("x64")).ToArray();
        Assert.Throws<ConfigException>(() => Build(Batch, BatchFields(four)));
    }

    [Fact]
    public void ASingleStringIsNotAListOfLeaves()
    {
        Assert.Throws<ConfigException>(() => Build(Batch, BatchFields(Leaf1)));
    }

    [Fact]
    public void BytesTakeHexOrAByteArray()
    {
        Assert.Equal("0x3082ab", Build(LockTs, new() { ["draw"] = "d", ["signature"] = new byte[] { 0x30, 0x82, 0xab } })["signature"]!.GetValue<string>());
        Assert.Equal("0x3082ab", Build(LockTs, new() { ["draw"] = "d", ["signature"] = "0x3082AB" })["signature"]!.GetValue<string>());
        Assert.Throws<ConfigException>(() => Build(LockTs, new() { ["draw"] = "d", ["signature"] = "3082" }));
        Assert.Throws<ConfigException>(() => Build(LockTs, new() { ["draw"] = "d", ["signature"] = "0x308" }));
    }

    [Fact]
    public void UndeclaredFieldsAreRefused()
    {
        var e = Assert.Throws<ConfigException>(() => Build(Events, new() { ["hash"] = Leaf1, ["draw"] = "d", ["extra"] = 1 }));

        Assert.Contains("extra", e.Message);
    }

    [Fact]
    public void MissingAndEmptyRequiredFieldsAreRefused()
    {
        Assert.Throws<ConfigException>(() => Build(Events, new() { ["hash"] = Leaf1 }));
        Assert.Throws<ConfigException>(() => Build(Events, new() { ["hash"] = Leaf1, ["draw"] = null }));
        Assert.Throws<ConfigException>(() => Build(Events, new() { ["hash"] = Leaf1, ["draw"] = "" }));
    }

    [Fact]
    public void OptionalFieldsMayBeLeftOut()
    {
        var definition = new EventTypeDefinition
        {
            Name = "t",
            RequestFields = [new("hash", FieldType.Bytes32, true, FieldRole.Key), new("note", FieldType.String)],
        };

        Assert.Equal($$"""{"hash":"{{Leaf1}}"}""", Build(definition, new() { ["hash"] = Leaf1, ["note"] = null }).ToJsonString());
    }
}
