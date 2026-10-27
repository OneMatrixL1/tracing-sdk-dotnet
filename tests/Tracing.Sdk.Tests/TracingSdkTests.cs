using System.Text;
using Tracing.Sdk.Events;
using Tracing.Sdk.Hash;

namespace Tracing.Sdk.Tests;

public class TracingSdkTests
{
    private static readonly string Hash1 = "0x" + new string('5', 64);

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
    public async Task TheDefaultAnchorTypeIsRegisteredWithoutConfig()
    {
        var (sdk, transport) = WithFakeTransport();

        await sdk.AnchorAsync("anchor", new Dictionary<string, object?> { ["hash"] = Hash1, ["signingTime"] = 1000 });

        Assert.Equal([("anchor", $$"""{"hash":"{{Hash1}}","signingTime":1000}""")], transport.EventCalls);
    }

    [Fact]
    public async Task ATransportFailureIsThrown()
    {
        var (sdk, transport) = WithFakeTransport();
        transport.Throw = true;

        await Assert.ThrowsAsync<TransportException>(() => sdk.AnchorAsync("anchor", new Dictionary<string, object?> { ["hash"] = Hash1, ["signingTime"] = 1 }));
    }

    [Fact]
    public void SameContentProducesTheSameHash()
    {
        var sdk = MakeSdk();

        Assert.Equal(sdk.Hash("{\"a\":1,\"b\":2}"), sdk.Hash("{\"b\":2,\"a\":1}"));
    }

    [Fact]
    public void StringAndBytesHashTheSame()
    {
        var sdk = MakeSdk();

        Assert.Equal(sdk.Hash("{\"é\":1}"), sdk.Hash(Encoding.UTF8.GetBytes("{\"é\":1}")));
    }

    [Fact]
    public void RawHashesInputByteForByte()
    {
        // Deliberately not valid JSON/XML — Raw must not try to parse it.
        const string rawData = "  not json, not xml, just bytes  ";

        Assert.Equal(Keccak(rawData), MakeSdk(SendOptions.ForDataType(DataType.Raw)).Hash(rawData));
    }

    [Fact]
    public void PerCallDataTypeOverridesTheConfigDefault()
    {
        const string rawData = "  not json, not xml, just bytes  ";

        Assert.Equal(Keccak(rawData), MakeSdk().Hash(rawData, SendOptions.ForDataType(DataType.Raw)));
    }

    [Fact]
    public void ConfigDataTypeIsUsedWhenOptionsOmitIt()
    {
        var sdk = MakeSdk();

        Assert.Equal(sdk.Hash("{\"b\":1,\"a\":2}"), sdk.Hash("{\"a\":2,\"b\":1}", new SendOptions()));
    }

    [Fact]
    public void DataTypeMayBeOmittedFromConfigWhenEveryCallSuppliesIt()
    {
        var sdk = new TracingSdk(new TracingSdkConfig { Endpoint = "https://indexer.example.com", Auth = AuthConfig.ApiToken("test-token") });

        Assert.Equal(32, sdk.Hash("{\"a\":1}", SendOptions.ForDataType(DataType.Json)).Length);
        Assert.Throws<ConfigException>(() => sdk.Hash("{\"a\":1}"));
    }

    [Fact]
    public async Task PerCallTimeoutIsPassedToTheTransport()
    {
        var (sdk, transport) = WithFakeTransport();
        var fields = new Dictionary<string, object?> { ["hash"] = Hash1, ["signingTime"] = 1 };

        await sdk.AnchorAsync("anchor", fields, SendOptions.ForTimeoutMs(2500));
        await sdk.AnchorAsync("anchor", fields);

        // No per-call value leaves the transport's own default in place.
        Assert.Equal([2500, null], transport.TimeoutCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeoutThrows(int timeoutMs) =>
        Assert.Throws<ConfigException>(() => new SendOptions(DataType.Json, timeoutMs));

    [Fact]
    public void SendOptionsWithMethodsReturnCopies()
    {
        var json = new SendOptions(DataType.Json);
        var xml = json.WithDataType(DataType.Xml);

        Assert.Equal(DataType.Json, json.DataType);
        Assert.Equal(DataType.Xml, xml.DataType);
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
}
