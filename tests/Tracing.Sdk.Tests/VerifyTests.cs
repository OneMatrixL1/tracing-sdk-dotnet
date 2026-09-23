using System.Text.Json;
using Tracing.Sdk.Hash;
using Tracing.Sdk.Verify;

namespace Tracing.Sdk.Tests;

public class VerifyTests
{
    private const string DataHash = "0x1c8aff950685c2ed4bc3174f3472287b56d9517b9c948127319a09a7a36deac8";
    private const string TxHash = "0x9f42bb1c5b1e2b7f2c1d8e3a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e";

    private readonly FakeRpcTransport _rpc = new();
    private readonly TracingSdk _sdk;

    public VerifyTests()
    {
        _sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = "https://indexer.example.com",
            Options = new SendOptions(DataType.Json, null, "https://rpc.example.com"),
            Auth = AuthConfig.ApiToken("test-token"),
        });
        _sdk.SetTransportForTesting(new FakeTransport());
        _sdk.SetRpcTransportForTesting(_rpc);
    }

    private static string Topic() => new AnchoredEventDecoder(new Keccak256Hasher()).Topic();

    /// <summary>A 32-byte word holding a uint64, right-aligned as the ABI encodes it.</summary>
    private static string Word(ulong value) => value.ToString("x").PadLeft(64, '0');

    private static string Receipt(params object[] logs) => JsonSerializer.Serialize(new { blockNumber = "0x10", logs });

    [Fact]
    public async Task VerifiesAnIndexedDataHashFromTopics()
    {
        _rpc.Receipt = Receipt(new { address = "0xAbC0000000000000000000000000000000000001", topics = new[] { Topic(), DataHash }, data = "0x" + Word(1700000000), logIndex = "0x2" });

        Assert.True(await _sdk.VerifyAsync(DataHash, TxHash));
    }

    [Fact]
    public async Task VerifiesANonIndexedDataHashFromLogData()
    {
        _rpc.Receipt = Receipt(new { topics = new[] { Topic() }, data = "0x" + DataHash[2..] + Word(1700000000) });

        Assert.True(await _sdk.VerifyAsync(DataHash, TxHash));
    }

    [Fact]
    public async Task AcceptsUppercaseAndUnprefixedHashes()
    {
        _rpc.Receipt = Receipt(new { topics = new[] { Topic().ToUpperInvariant(), DataHash.ToUpperInvariant() }, data = "0x" + Word(1) });

        Assert.True(await _sdk.VerifyAsync(DataHash[2..].ToUpperInvariant(), TxHash));
    }

    [Fact]
    public async Task FindsTheAnchorAmongUnrelatedLogs()
    {
        _rpc.Receipt = Receipt(
            new { topics = new[] { "0x" + string.Concat(Enumerable.Repeat("11", 32)), DataHash }, data = "0x" },
            new { topics = new[] { Topic(), "0x" + string.Concat(Enumerable.Repeat("ab", 32)) }, data = "0x" + Word(5) },
            new { topics = new[] { Topic(), DataHash }, data = "0x" + Word(7) });

        Assert.True(await _sdk.VerifyAsync(DataHash, TxHash));
    }

    [Fact]
    public async Task ReturnsFalseWhenTheAnchoredHashDiffers()
    {
        _rpc.Receipt = Receipt(new { topics = new[] { Topic(), "0x" + string.Concat(Enumerable.Repeat("cd", 32)) }, data = "0x" + Word(1700000000) });

        Assert.False(await _sdk.VerifyAsync(DataHash, TxHash));
    }

    [Fact]
    public async Task ReturnsFalseWhenTheTransactionHasNoAnchoredEvent()
    {
        _rpc.Receipt = Receipt();

        Assert.False(await _sdk.VerifyAsync(DataHash, TxHash));
    }

    [Fact]
    public async Task ReturnsFalseWhenTheEventIsTruncated()
    {
        // topics[0] matches but the uint64 argument is missing entirely.
        _rpc.Receipt = Receipt(new { topics = new[] { Topic(), DataHash }, data = "0x" });

        Assert.False(await _sdk.VerifyAsync(DataHash, TxHash));
    }

    [Fact]
    public async Task PassesTheNormalizedTxHashAndConfiguredRpcUrlToTheTransport()
    {
        _rpc.Receipt = Receipt();

        await _sdk.VerifyAsync(DataHash, TxHash.ToUpperInvariant());

        Assert.Equal([("https://rpc.example.com", TxHash, (int?)null)], _rpc.Calls);
    }

    [Fact]
    public async Task PerCallOptionsOverrideRpcUrlAndTimeout()
    {
        _rpc.Receipt = Receipt();

        await _sdk.VerifyAsync(DataHash, TxHash, TracingSdk.ModeTransactionHash, new SendOptions(null, 250, "https://other-rpc.example.com"));

        Assert.Equal([("https://other-rpc.example.com", TxHash, (int?)250)], _rpc.Calls);
    }

    [Fact]
    public async Task ThrowsWhenNoRpcUrlIsConfigured()
    {
        using var sdk = new TracingSdk(new TracingSdkConfig
        {
            Endpoint = "https://indexer.example.com",
            Options = SendOptions.ForDataType(DataType.Json),
            Auth = AuthConfig.ApiToken("test-token"),
        });
        sdk.SetRpcTransportForTesting(_rpc);

        await Assert.ThrowsAsync<ConfigException>(() => sdk.VerifyAsync(DataHash, TxHash));
    }

    [Theory]
    [InlineData(DataHash, TxHash, "blockNumber")]
    [InlineData("", TxHash, TracingSdk.ModeTransactionHash)]
    [InlineData(DataHash, "0xnot-a-hash", TracingSdk.ModeTransactionHash)]
    public async Task RejectsBadArguments(string dataHash, string proof, string mode) =>
        await Assert.ThrowsAsync<ConfigException>(() => _sdk.VerifyAsync(dataHash, proof, mode));

    [Fact]
    public async Task ThrowsWhenTheNodeDoesNotKnowTheTransaction()
    {
        _rpc.Receipt = null;

        await Assert.ThrowsAsync<TransportException>(() => _sdk.VerifyAsync(DataHash, TxHash));
    }

    [Fact]
    public async Task PropagatesRpcTransportFailures()
    {
        _rpc.Throw = true;

        await Assert.ThrowsAsync<TransportException>(() => _sdk.VerifyAsync(DataHash, TxHash));
    }

    [Fact]
    public void TopicIsTheKeccakOfTheEventSignature() =>
        Assert.Equal(new Keccak256Hasher().Hash("Anchored(bytes32,uint64)"u8.ToArray()), Topic());

    [Fact]
    public void DecodesSigningTimeAsAnExactUint64()
    {
        var log = JsonDocument.Parse(JsonSerializer.Serialize(new { topics = new[] { Topic(), DataHash }, data = "0x" + Word(ulong.MaxValue) })).RootElement;

        Assert.Equal(new AnchoredEvent(DataHash, ulong.MaxValue), new AnchoredEventDecoder(new Keccak256Hasher()).Decode(log));
    }
}
