using System.Security.Cryptography;
using System.Text;
using Tracing.Sdk.Events;
using Tracing.Sdk.Hash;

namespace Tracing.Sdk.Tests;

public class HashesTests
{
    private static readonly Keccak256Hasher Keccak = new();

    private static TracingSdk MakeSdk(EventTypeRegistry? eventTypes = null, DataType? dataType = DataType.Json) => new(new TracingSdkConfig
    {
        Endpoint = "https://indexer.example.com",
        Auth = AuthConfig.ApiToken("t"),
        Options = new SendOptions(dataType),
        EventTypes = eventTypes ?? EventTypeRegistry.Default().Register(EventPresets.Tickets),
    });

    [Fact]
    public void RecordIsKeccakOfTheCanonicalBytes()
    {
        Assert.Equal(Keccak.Hash(Encoding.UTF8.GetBytes("""{"a":1,"b":2}""")), Hashes.Record("""{ "b": 2, "a": 1 }""", DataType.Json));
    }

    [Fact]
    public void RecordMatchesTracingSdkHash()
    {
        using var sdk = MakeSdk();

        Assert.Equal(sdk.Hash("<a><b>1</b></a>", SendOptions.ForDataType(DataType.Xml)), Hashes.Record("<a><b>1</b></a>", DataType.Xml));
    }

    [Fact]
    public void LeafIsKeccakOfPrefixAndCanonicalBytes()
    {
        Assert.Equal(Keccak.Hash([0x00, .. "{\"n\":1}"u8.ToArray()]), Hashes.Leaf("""{ "n" : 1 }""", DataType.Json));
        Assert.Equal(Keccak.Hash([0x07, .. "raw"u8.ToArray()]), Hashes.Leaf("raw", DataType.Raw, 0x07));
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    public void ReservedLeafPrefixesAreRefused(byte prefix)
    {
        Assert.Throws<ConfigException>(() => Hashes.Leaf("x", DataType.Raw, prefix));
    }

    [Fact]
    public void LeavesKeepTheOrderGiven()
    {
        var leaves = Hashes.Leaves(["b", "a"], DataType.Raw);

        Assert.Equal([Hashes.Leaf("b", DataType.Raw), Hashes.Leaf("a", DataType.Raw)], leaves);
    }

    [Fact]
    public void DrawHashIsKeccakOfUtf8()
    {
        Assert.Equal(
            "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470",
            Keccak256Hasher.ToHex(Hashes.DrawHash("")));
        Assert.Equal(Keccak.Hash(Encoding.UTF8.GetBytes("kỳ-1")), Hashes.DrawHash("kỳ-1"));
    }

    [Fact]
    public void LockTimestampImprintIsSha256OfTheRawDrawRoot()
    {
        var drawRoot = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var expected = SHA256.HashData(drawRoot);

        Assert.Equal(expected, Hashes.LockTimestampImprint(drawRoot));
        Assert.Equal(expected, Hashes.LockTimestampImprint(Keccak256Hasher.ToHex(drawRoot)));
        Assert.Equal(expected, Hashes.LockTimestampImprint(Convert.ToHexString(drawRoot)));
        Assert.Throws<ConfigException>(() => Hashes.LockTimestampImprint("0x1234"));
    }

    [Fact]
    public void HashForAMerkleTypeIsALeaf()
    {
        using var sdk = MakeSdk();

        Assert.Equal(Hashes.Leaf("""{"n":1}""", DataType.Json), sdk.HashFor(Tickets.Batch, """{"n":1}"""));
        Assert.Equal([Hashes.Leaf("[1]", DataType.Json), Hashes.Leaf("[2]", DataType.Json)], sdk.LeavesFor(Tickets.Batch, ["[1]", "[2]"]));
    }

    [Fact]
    public void HashForADirectTypeIsARecordHash()
    {
        using var sdk = MakeSdk();

        Assert.Equal(Hashes.Record("""{"n":1}""", DataType.Json), sdk.HashFor(Tickets.Events, """{"n":1}"""));
        Assert.Throws<ConfigException>(() => sdk.LeavesFor(Tickets.Events, ["x"]));
    }

    [Fact]
    public void TheTypesHashingWinsOverTheOptions()
    {
        var registry = EventTypeRegistry.FromJson("""
            [{ "name": "doc", "request": { "fields": [ { "name": "hash", "type": "bytes32", "role": "key" } ] }, "hashing": { "dataType": "raw" } },
             { "name": "tree", "anchoring": { "mode": "merkle" }, "request": { "fields": [ { "name": "l", "type": "bytes32[]", "role": "leaves" } ] }, "hashing": { "leafPrefix": "0x09" } }]
            """);
        using var sdk = MakeSdk(registry);

        Assert.Equal(Hashes.Record(" {} ", DataType.Raw), sdk.HashFor("doc", " {} ", SendOptions.ForDataType(DataType.Json)));
        Assert.Equal(Hashes.Leaf(" {} ", DataType.Json, 0x09), sdk.HashFor("tree", " {} "));
    }

    [Fact]
    public void HashForWithoutAnyDataTypeIsAConfigError()
    {
        using var sdk = MakeSdk(dataType: null);

        Assert.Throws<ConfigException>(() => sdk.HashFor(Tickets.Events, "{}"));
        Assert.Throws<ConfigException>(() => sdk.HashFor("nope", "{}"));
    }
}
