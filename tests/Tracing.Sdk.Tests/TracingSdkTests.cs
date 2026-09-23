using System.Text;
using Tracing.Sdk.Hash;

namespace Tracing.Sdk.Tests;

public class TracingSdkTests
{
    private static TracingSdk MakeSdk(SendOptions? options = null, AuthConfig? auth = null) => new(new TracingSdkConfig
    {
        Endpoint = "https://indexer.example.com",
        Options = options ?? SendOptions.ForDataType(DataType.Json),
        Auth = auth ?? AuthConfig.ApiToken("test-token"),
    });

    private static (TracingSdk Sdk, FakeTransport Transport) WithFakeTransport(TracingSdk? sdk = null)
    {
        sdk ??= MakeSdk();
        var transport = new FakeTransport();
        sdk.SetTransportForTesting(transport);

        return (sdk, transport);
    }

    private static byte[] Keccak(string input) => new Keccak256Hasher().Hash(Encoding.UTF8.GetBytes(input));

    [Fact]
    public async Task SendReturnsTheHashAndTheResponse()
    {
        var (sdk, _) = WithFakeTransport();

        var result = await sdk.SendAsync("{\"secret\":\"do-not-leak\"}", 1234);

        Assert.Equal(32, result.Hash.Length);
        Assert.Equal(new IndexerResponse(200, "", null, 1), result.Response);
    }

    [Fact]
    public async Task SendPassesTheHashAndSigningTimeToTheTransport()
    {
        var (sdk, transport) = WithFakeTransport();

        var result = await sdk.SendAsync("{\"a\":1}", 1000);

        var entry = Assert.Single(transport.SingleCalls);
        Assert.Equal(Keccak256Hasher.ToHex(result.Hash), entry.Hash);
        Assert.Equal(1000, entry.SigningTime);
        Assert.Empty(transport.BatchCalls);
    }

    [Fact]
    public async Task SameContentProducesTheSameHash()
    {
        var (sdk, _) = WithFakeTransport();

        var a = await sdk.SendAsync("{\"a\":1,\"b\":2}", 1);
        var b = await sdk.SendAsync("{\"b\":2,\"a\":1}", 2);

        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public async Task StringAndBytesHashTheSame()
    {
        var sdk = MakeSdk();

        Assert.Equal(sdk.Hash("{\"é\":1}"), sdk.Hash(Encoding.UTF8.GetBytes("{\"é\":1}")));
        Assert.Equal((await WithFakeTransport(sdk).Sdk.SendAsync("{\"é\":1}", 1)).Hash, sdk.Hash("{\"é\":1}"));
    }

    [Fact]
    public async Task SendWithoutSigningTimeThrowsWithoutSending()
    {
        var (sdk, transport) = WithFakeTransport();

        await Assert.ThrowsAsync<ConfigException>(() => sdk.SendAsync("{\"a\":1}", null!));
        Assert.Empty(transport.SingleCalls);
    }

    [Fact]
    public async Task SendThrowsOnTransportFailure()
    {
        var (sdk, transport) = WithFakeTransport();
        transport.Throw = true;

        await Assert.ThrowsAsync<TransportException>(() => sdk.SendAsync("{\"a\":1}", 1000));
    }

    [Fact]
    public async Task SendBatchReturnsOneResultPerRecordSharingTheResponse()
    {
        var (sdk, _) = WithFakeTransport();

        var results = await sdk.SendBatchAsync([new BatchRecord("{\"a\":1}", 1), new BatchRecord("{\"a\":2}", 2)]);

        Assert.Equal(2, results.Count);
        Assert.NotEqual(results[0].Hash, results[1].Hash);
        Assert.Equal(2, results[0].Response.RecordCount);
        Assert.Same(results[0].Response, results[1].Response);
    }

    [Fact]
    public async Task SendBatchSendsAllEntriesInOneRequest()
    {
        var (sdk, transport) = WithFakeTransport();

        var results = await sdk.SendBatchAsync([new BatchRecord("{\"a\":1}", 1), new BatchRecord("{\"a\":2}", 2)]);

        var batch = Assert.Single(transport.BatchCalls);
        Assert.Equal([Keccak256Hasher.ToHex(results[0].Hash), Keccak256Hasher.ToHex(results[1].Hash)], batch.Select(e => e.Hash));
        Assert.Equal([1, 2], batch.Select(e => (int)e.SigningTime));
        Assert.Empty(transport.SingleCalls);
    }

    public static TheoryData<BatchRecord?> InvalidRecords => new()
    {
        { new BatchRecord("{\"a\":1}", null!) },
        { new BatchRecord((string)null!, 1) },
        { new BatchRecord((byte[])null!, 1) },
        { null },
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public async Task SendBatchRejectsAnIncompleteRecordAndSendsNothing(BatchRecord? record)
    {
        var (sdk, transport) = WithFakeTransport();

        await Assert.ThrowsAsync<ConfigException>(() => sdk.SendBatchAsync([new BatchRecord("{\"a\":1}", 1), record!]));
        // Nothing is sent, so one bad record can't half-anchor a batch.
        Assert.Empty(transport.BatchCalls);
    }

    [Fact]
    public async Task SendBatchThrowsOnTransportFailure()
    {
        var (sdk, transport) = WithFakeTransport();
        transport.Throw = true;

        await Assert.ThrowsAsync<TransportException>(() => sdk.SendBatchAsync([new BatchRecord("{\"a\":1}", 1)]));
    }

    [Fact]
    public async Task RawHashesInputByteForByte()
    {
        var (sdk, _) = WithFakeTransport(MakeSdk(SendOptions.ForDataType(DataType.Raw)));
        // Deliberately not valid JSON/XML — Raw must not try to parse it.
        const string rawData = "  not json, not xml, just bytes  ";

        Assert.Equal(Keccak(rawData), (await sdk.SendAsync(rawData, 1000)).Hash);
    }

    [Fact]
    public async Task PerCallDataTypeOverridesTheConfigDefault()
    {
        var (sdk, _) = WithFakeTransport();
        const string rawData = "  not json, not xml, just bytes  ";

        Assert.Equal(Keccak(rawData), (await sdk.SendAsync(rawData, 1000, SendOptions.ForDataType(DataType.Raw))).Hash);
    }

    [Fact]
    public async Task PerCallDataTypeOverridesTheConfigDefaultForBatches()
    {
        var (sdk, _) = WithFakeTransport();
        const string xml = "<order><id>1</id></order>";

        var results = await sdk.SendBatchAsync([new BatchRecord(xml, 1)], SendOptions.ForDataType(DataType.Xml));

        Assert.Equal(MakeSdk(SendOptions.ForDataType(DataType.Xml)).Hash(xml), results[0].Hash);
    }

    [Fact]
    public async Task ConfigDataTypeIsUsedWhenOptionsOmitIt()
    {
        var (sdk, _) = WithFakeTransport();

        var withoutOptions = await sdk.SendAsync("{\"b\":1,\"a\":2}", 1);
        var withEmptyOptions = await sdk.SendAsync("{\"a\":2,\"b\":1}", 2, new SendOptions());

        Assert.Equal(withoutOptions.Hash, withEmptyOptions.Hash);
    }

    [Fact]
    public async Task PerCallTimeoutIsPassedToTheTransport()
    {
        var (sdk, transport) = WithFakeTransport();

        await sdk.SendAsync("{\"a\":1}", 1, SendOptions.ForDataType(DataType.Json).WithTimeoutMs(2500));
        await sdk.SendBatchAsync([new BatchRecord("{\"a\":1}", 1)], SendOptions.ForTimeoutMs(500));
        await sdk.QueryByHashAsync("0xdeadbeef", SendOptions.ForTimeoutMs(750));

        Assert.Equal([2500, 500, 750], transport.TimeoutCalls);
    }

    [Fact]
    public async Task CallsWithoutATimeoutKeepTheTransportDefault()
    {
        var (sdk, transport) = WithFakeTransport(MakeSdk(new SendOptions(DataType.Json, 4000)));

        await sdk.SendAsync("{\"a\":1}", 1);
        await sdk.SendAsync("{\"a\":1}", 1, SendOptions.ForDataType(DataType.Json));

        Assert.Equal([null, null], transport.TimeoutCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeoutThrows(int timeoutMs) =>
        Assert.Throws<ConfigException>(() => new SendOptions(DataType.Json, timeoutMs));

    [Fact]
    public void BlankRpcUrlThrows() => Assert.Throws<ConfigException>(() => SendOptions.ForRpcUrl("   "));

    [Fact]
    public void SendOptionsWithMethodsReturnCopies()
    {
        var json = new SendOptions(DataType.Json);
        var xml = json.WithDataType(DataType.Xml);

        Assert.Equal(DataType.Json, json.DataType);
        Assert.Equal(DataType.Xml, xml.DataType);
    }

    [Fact]
    public async Task DataTypeMayBeOmittedFromConfigWhenEveryCallSuppliesIt()
    {
        var (sdk, _) = WithFakeTransport(new TracingSdk(new TracingSdkConfig
        {
            Endpoint = "https://indexer.example.com",
            Auth = AuthConfig.ApiToken("test-token"),
        }));

        Assert.Equal(32, (await sdk.SendAsync("{\"a\":1}", 1, SendOptions.ForDataType(DataType.Json))).Hash.Length);
    }

    [Fact]
    public async Task SendWithoutAnyDataTypeThrowsWithoutSending()
    {
        var (sdk, transport) = WithFakeTransport(new TracingSdk(new TracingSdkConfig
        {
            Endpoint = "https://indexer.example.com",
            Auth = AuthConfig.ApiToken("test-token"),
        }));

        await Assert.ThrowsAsync<ConfigException>(() => sdk.SendAsync("{\"a\":1}", 1));
        Assert.Empty(transport.SingleCalls);
    }

    [Fact]
    public void RawDataWithALoneSurrogateIsRejected() =>
        Assert.Throws<CanonicalizationException>(() => MakeSdk(SendOptions.ForDataType(DataType.Raw)).Hash("a\uD800b"));

    [Fact]
    public void MissingEndpointOrAuthValuesThrow()
    {
        Assert.Throws<ConfigException>(() => new TracingSdk(new TracingSdkConfig { Endpoint = "", Auth = AuthConfig.ApiToken("t") }));
        Assert.Throws<ConfigException>(() => new TracingSdk(new TracingSdkConfig { Endpoint = "https://x", Auth = null! }));
        Assert.Throws<ConfigException>(() => MakeSdk(auth: AuthConfig.ApiToken("")));
        Assert.Throws<ConfigException>(() => MakeSdk(auth: AuthConfig.Basic("user", "")));
    }

    [Fact]
    public void MissingMtlsFilesFailAtConstruction() =>
        Assert.Throws<ConfigException>(() => MakeSdk(auth: AuthConfig.Mtls("/nonexistent/client.crt", "/nonexistent/client.key")));

    [Fact]
    public void AuthToStringDoesNotLeakSecrets()
    {
        Assert.DoesNotContain("s3cret", AuthConfig.Basic("user", "s3cret").ToString());
        Assert.DoesNotContain("s3cret", AuthConfig.Mtls("c", "k", null, "s3cret").ToString());
    }

    [Fact]
    public async Task QueryByHashReturnsTheHashAndEveryProof()
    {
        var (sdk, transport) = WithFakeTransport();

        var result = await sdk.QueryByHashAsync("0xdeadbeef");

        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], result.Hash);
        Assert.Equal([Convert.FromHexString(FakeTransport.ProofA[2..]), Convert.FromHexString(FakeTransport.ProofB[2..])], result.Proof);
        Assert.Equal("transactionHash", result.ProofType);
        Assert.Equal(["0xdeadbeef"], transport.QueryCalls);
    }

    [Fact]
    public async Task QueryByHashAcceptsUppercaseAndUnprefixedHex()
    {
        var (sdk, transport) = WithFakeTransport();
        transport.QueryResponse = FakeTransport.Answer(200, """{"hash":"DEADBEEF","proof":["0XABCD"]}""");

        var result = await sdk.QueryByHashAsync("0xdeadbeef");

        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], result.Hash);
        Assert.Equal([0xAB, 0xCD], Assert.Single(result.Proof));
    }

    [Fact]
    public async Task QueryByHashReturnsAnEmptyProofWhenTheIndexerReportsNone()
    {
        var (sdk, transport) = WithFakeTransport();
        transport.QueryResponse = FakeTransport.Answer(200, """{"hash":"0xdeadbeef","proof":[],"proofType":"transactionHash"}""");

        Assert.Empty((await sdk.QueryByHashAsync("0xdeadbeef")).Proof);
    }

    [Fact]
    public async Task QueryByHashDefaultsProofTypeToTransactionHash()
    {
        var (sdk, transport) = WithFakeTransport();
        transport.QueryResponse = FakeTransport.Answer(200, """{"hash":"0xdeadbeef","proof":["0xabcd"]}""");

        Assert.Equal(TracingSdk.ModeTransactionHash, (await sdk.QueryByHashAsync("0xdeadbeef")).ProofType);
    }

    [Theory]
    [InlineData(200, """{"hash":"0xdeadbeef","proof":"0xabcd"}""")]
    [InlineData(200, """{"hash":"0xdeadbeef","proof":["0xabc"]}""")]
    [InlineData(200, """{"hash":"0xdeadbeef","proof":["0xzz"]}""")]
    [InlineData(200, """{"hash":"0xdeadbeef","proof":[""]}""")]
    [InlineData(200, """{"hash":"0xdeadbeef","proof":[42]}""")]
    [InlineData(200, """{"hash":"not-hex","proof":[]}""")]
    [InlineData(200, "not-json-object")]
    [InlineData(200, "[]")]
    [InlineData(404, "null")]
    public async Task QueryByHashRejectsAnUnexpectedAnswer(int statusCode, string body)
    {
        var (sdk, transport) = WithFakeTransport();
        transport.QueryResponse = FakeTransport.Answer(statusCode, body);

        await Assert.ThrowsAsync<TransportException>(() => sdk.QueryByHashAsync("0xdeadbeef"));
    }

    [Fact]
    public async Task QueryByHashAcceptsTheDigestHashReturns()
    {
        var (sdk, transport) = WithFakeTransport();
        var hash = sdk.Hash("{\"a\":1}");

        await sdk.QueryByHashAsync(hash);

        Assert.Equal([Keccak256Hasher.ToHex(hash)], transport.QueryCalls);
    }

    [Fact]
    public async Task QueryByHashRejectsAnEmptyHash()
    {
        var (sdk, _) = WithFakeTransport();

        await Assert.ThrowsAsync<ConfigException>(() => sdk.QueryByHashAsync(""));
        await Assert.ThrowsAsync<ConfigException>(() => sdk.QueryByHashAsync(Array.Empty<byte>()));
    }

    [Fact]
    public async Task QueryByHashThrowsOnTransportFailure()
    {
        var (sdk, transport) = WithFakeTransport();
        transport.Throw = true;

        await Assert.ThrowsAsync<TransportException>(() => sdk.QueryByHashAsync("0xdeadbeef"));
    }
}
