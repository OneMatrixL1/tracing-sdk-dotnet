using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;

namespace Tracing.Sdk.Examples;

/// <summary>
/// A stand-in RFC 3161 timestamp authority for trying lockts without a real
/// one. It signs with a throwaway self-signed certificate whose only, critical,
/// extended key usage is timestamping, which is what the backend checks. An
/// Indexer built with trusted TSA roots (params.tsaRootsPEM) refuses it, as it
/// should: the token proves the drawRoot, not who issued it. Never use it for
/// a real draw.
/// </summary>
internal static class MockTsa
{
    /// <summary>
    /// A DER TimeStampResp, status granted, over the SHA-256 imprint given
    /// (see Hashes.LockTimestampImprint), with the authority's certificate inside.
    /// </summary>
    public static byte[] TimeStamp(byte[] sha256Imprint)
    {
        var random = new SecureRandom();
        var keys = new RsaKeyPairGenerator();
        keys.Init(new KeyGenerationParameters(random, 2048));
        var key = keys.GenerateKeyPair();
        var certificate = SelfSignedTsaCertificate(key, random);

        var tokens = new TimeStampTokenGenerator(key.Private, certificate, TspAlgorithms.Sha256, "1.3.6.1.4.1.99999.1");
        tokens.SetCertificates(CollectionUtilities.CreateStore(new[] { certificate }));

        var requests = new TimeStampRequestGenerator();
        requests.SetCertReq(true);
        var request = requests.Generate(TspAlgorithms.Sha256, sha256Imprint, new BigInteger(64, random));

        var response = new TimeStampResponseGenerator(tokens, TspAlgorithms.Allowed)
            .Generate(request, new BigInteger(64, random), DateTime.UtcNow);
        response.Validate(request);

        return response.GetEncoded();
    }

    private static X509Certificate SelfSignedTsaCertificate(AsymmetricCipherKeyPair key, SecureRandom random)
    {
        var name = new X509Name("CN=Mock TSA (examples only)");
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(new BigInteger(64, random));
        generator.SetIssuerDN(name);
        generator.SetSubjectDN(name);
        generator.SetNotBefore(DateTime.UtcNow.AddHours(-1));
        generator.SetNotAfter(DateTime.UtcNow.AddDays(1));
        generator.SetPublicKey(key.Public);
        generator.AddExtension(X509Extensions.ExtendedKeyUsage, critical: true, new ExtendedKeyUsage(KeyPurposeID.id_kp_timeStamping));

        return generator.Generate(new Asn1SignatureFactory("SHA256WITHRSA", key.Private, random));
    }
}
