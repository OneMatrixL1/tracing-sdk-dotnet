using System.Text;
using Tracing.Sdk.Canonicalize;
using Tracing.Sdk.Hash;

namespace Tracing.Sdk.Tests;

public class CanonicalizerTests
{
    public static IEnumerable<object[]> JsonCases() => Fixtures.Cases("json-canonicalize.json");

    public static IEnumerable<object[]> XmlCases() => Fixtures.Cases("xml-canonicalize.json");

    public static IEnumerable<object[]> KeccakCases() => Fixtures.Cases("keccak256.json");

    [Theory]
    [MemberData(nameof(JsonCases))]
    public void JsonFixture(FixtureCase fixture) => AssertFixture(new JsonCanonicalizer(), fixture);

    [Theory]
    [MemberData(nameof(XmlCases))]
    public void XmlFixture(FixtureCase fixture) => AssertFixture(new XmlCanonicalizer(), fixture);

    [Theory]
    [MemberData(nameof(KeccakCases))]
    public void KeccakFixture(FixtureCase fixture) =>
        Assert.Equal(fixture.ExpectedHash, new Keccak256Hasher().Hash(Encoding.UTF8.GetBytes(fixture.Inputs[0])));

    [Fact]
    public void JsonRejectsInvalidUtf8Bytes() =>
        Assert.Throws<CanonicalizationException>(() => new JsonCanonicalizer().Canonicalize([0x22, 0xFF, 0x22]));

    [Fact]
    public void XmlDecodesBytesUsingTheDeclaredEncoding()
    {
        byte[] latin1 = [.. Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><r>caf"), 0xE9, .. Encoding.ASCII.GetBytes("</r>")];

        Assert.Equal("<r>café</r>", Encoding.UTF8.GetString(new XmlCanonicalizer().Canonicalize(latin1)));
    }

    [Fact]
    public void XmlRejectsAnUnknownDeclaredEncoding() =>
        Assert.Throws<CanonicalizationException>(() =>
            new XmlCanonicalizer().Canonicalize(Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"x-nope\"?><r/>")));

    [Fact]
    public void RawReturnsInputUnchanged()
    {
        var input = Encoding.UTF8.GetBytes("  {\"b\":1,\"a\":2}  ");

        Assert.Equal(input, new RawCanonicalizer().Canonicalize(input));
        Assert.Empty(new RawCanonicalizer().Canonicalize([]));
    }

    private static void AssertFixture(ICanonicalizer canonicalizer, FixtureCase fixture)
    {
        foreach (var input in fixture.Inputs)
        {
            var bytes = Encoding.UTF8.GetBytes(input);

            if (fixture.ExpectError)
            {
                Assert.Throws<CanonicalizationException>(() => canonicalizer.Canonicalize(bytes));
            }
            else if (fixture.AllowError)
            {
                try
                {
                    Assert.DoesNotContain(fixture.ForbiddenSubstring!, Encoding.UTF8.GetString(canonicalizer.Canonicalize(bytes)));
                }
                catch (CanonicalizationException)
                {
                    // Rejecting the document outright also satisfies the case.
                }
            }
            else
            {
                Assert.Equal(fixture.ExpectedOutput, Encoding.UTF8.GetString(canonicalizer.Canonicalize(bytes)));
            }
        }
    }
}
