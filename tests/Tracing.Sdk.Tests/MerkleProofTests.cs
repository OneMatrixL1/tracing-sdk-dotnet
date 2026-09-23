using System.Text.Json;
using Tracing.Sdk.Hash;
using Tracing.Sdk.Verify;

namespace Tracing.Sdk.Tests;

/// <summary>
/// Cases come from testdata/merkle.json (shared with every other language
/// SDK), whose roots were computed by merkletreejs, independently of this code.
/// </summary>
public class MerkleProofTests
{
    private const string TxHash = "0x9f42bb1c5b1e2b7f2c1d8e3a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e";

    private readonly FakeRpcTransport _rpc = new();
    private readonly TracingSdk _sdk;

    public MerkleProofTests()
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

    public static IEnumerable<object[]> Cases() => Fixtures.Cases("merkle.json");

    private static FixtureCase Case(string id) =>
        Cases().Select(c => (FixtureCase)c[0]).Single(c => c.Id == id);

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex[2..]);

    private static string Topic() => new AnchoredEventDecoder(new Keccak256Hasher()).Topic();

    /// <summary>A receipt whose one Anchored event carries <paramref name="anchored"/>.</summary>
    private static string ReceiptAnchoring(string anchored) => JsonSerializer.Serialize(new
    {
        logs = new[] { new { topics = new[] { Topic(), anchored }, data = "0x" + 1700000000UL.ToString("x").PadLeft(64, '0') } },
    });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ComputeRootMatchesTheFixture(FixtureCase fixture)
    {
        var root = Keccak256Hasher.ToHex(MerkleProof.ComputeRoot(Bytes(fixture.Leaf), fixture.Proof.Select(Bytes)));

        Assert.Equal(fixture.Valid, root == fixture.Root);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task VerifyAgainstATransactionAnchoringTheRoot(FixtureCase fixture)
    {
        _rpc.Receipt = ReceiptAnchoring(fixture.Root);

        var verified = await _sdk.VerifyAsync(Bytes(fixture.Leaf), [Bytes(TxHash), .. fixture.Proof.Select(Bytes)], TracingSdk.ModeMerkleProof);

        Assert.Equal(fixture.Valid, verified);
        // The first proof element is the transaction, and only it is fetched.
        Assert.Equal([("https://rpc.example.com", TxHash, (int?)null)], _rpc.Calls);
    }

    [Fact]
    public void PairHashingIgnoresTheOrderOfTheTwoNodes()
    {
        var (a, b) = (Bytes(TxHash), Bytes(Case("two-leaf-tree-left").Root));

        Assert.Equal(MerkleProof.HashPair(a, b), MerkleProof.HashPair(b, a));
    }

    [Fact]
    public async Task VerifiesWithHexStrings()
    {
        var fixture = Case("five-leaf-tree-leaf-0");
        _rpc.Receipt = ReceiptAnchoring(fixture.Root);

        Assert.True(await _sdk.VerifyAsync(fixture.Leaf.ToUpperInvariant(), [TxHash, .. fixture.Proof], TracingSdk.ModeMerkleProof));
    }

    [Fact]
    public async Task VerifiesWhatQueryByHashReturns()
    {
        var fixture = Case("sixteen-leaf-tree-leaf-7");
        var proofJson = JsonSerializer.Serialize(new[] { TxHash }.Concat(fixture.Proof));
        _sdk.SetTransportForTesting(new FakeTransport
        {
            QueryResponse = FakeTransport.Answer(200, $$"""{"hash":"{{fixture.Leaf}}","proof":{{proofJson}},"proofType":"merkleProof"}"""),
        });
        _rpc.Receipt = ReceiptAnchoring(fixture.Root);

        var anchor = await _sdk.QueryByHashAsync(fixture.Leaf);

        Assert.Equal(TracingSdk.ModeMerkleProof, anchor.ProofType);
        Assert.True(await _sdk.VerifyAsync(anchor.Hash, anchor.Proof, anchor.ProofType));
    }

    [Fact]
    public async Task ReturnsFalseWhenTheTransactionAnchorsAnotherRoot()
    {
        var fixture = Case("five-leaf-tree-leaf-0");
        _rpc.Receipt = ReceiptAnchoring(Case("sixteen-leaf-tree-leaf-0").Root);

        Assert.False(await _sdk.VerifyAsync(fixture.Leaf, [TxHash, .. fixture.Proof], TracingSdk.ModeMerkleProof));
    }

    [Fact]
    public async Task AOneElementProofIsASingleLeafTree()
    {
        var fixture = Case("single-leaf-tree");
        _rpc.Receipt = ReceiptAnchoring(fixture.Root);

        Assert.True(await _sdk.VerifyAsync(Bytes(fixture.Leaf), Bytes(TxHash), TracingSdk.ModeMerkleProof));
    }

    [Fact]
    public async Task RejectsAnEmptyProof() =>
        await Assert.ThrowsAsync<ConfigException>(() =>
            _sdk.VerifyAsync(Case("single-leaf-tree").Leaf, Array.Empty<string>(), TracingSdk.ModeMerkleProof));

    [Fact]
    public async Task RejectsASiblingThatIsNot32Bytes()
    {
        var e = await Assert.ThrowsAsync<ConfigException>(() =>
            _sdk.VerifyAsync(Bytes(TxHash), [Bytes(TxHash), new byte[31]], TracingSdk.ModeMerkleProof));

        Assert.Contains("proof[1]", e.Message);
    }

    [Fact]
    public async Task ThrowsWhenTheNodeDoesNotKnowTheTransaction()
    {
        _rpc.Receipt = null;

        await Assert.ThrowsAsync<TransportException>(() => _sdk.VerifyAsync(TxHash, [TxHash], TracingSdk.ModeMerkleProof));
    }

    [Fact]
    public async Task TransactionHashModeChecksEachListedTransactionUntilOneMatches()
    {
        var leaf = Case("single-leaf-tree").Leaf;
        _rpc.Receipt = ReceiptAnchoring(leaf);
        string[] transactions = [TxHash, "0x" + string.Concat(Enumerable.Repeat("ee", 32))];

        Assert.True(await _sdk.VerifyAsync(leaf, transactions, TracingSdk.ModeTransactionHash));
        // The first transaction already matched, so the second is never fetched.
        Assert.Single(_rpc.Calls);
    }

    [Fact]
    public async Task RejectsAnUnknownMode() =>
        await Assert.ThrowsAsync<ConfigException>(() => _sdk.VerifyAsync(TxHash, [TxHash], "blockNumber"));
}
