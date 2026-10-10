using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.DesktopClient.Services;
using SimplArchive.ApiClient;

namespace SimplArchive.DesktopUiEndToEndTests;

// The card's key-agreement path against the ENCRYPTION SERVICE's own envelope shape (#1498, service ADR 0018).
//
// WHAT THIS ADDS, because a sibling already exists. KeyAgreementEnvelopeTests covers the same opener against
// an **OpenSSL**-produced fixture, and that is the right instinct — a round trip against our own writer agrees
// with whatever this code assumed. But OpenSSL and BouncyCastle do not produce the same bytes, and the
// encryption service produces CMS with BouncyCastle (its ADR 0018, because managed EnvelopedCms cannot address
// an EC recipient at all). Two producers, two fixtures:
//
//   * OpenSSL      -- sha1kdf, and `keyInfo` with its parameters ABSENT, which RFC 3565 §2.2 requires
//   * BouncyCastle -- sha256kdf, and `keyInfo` with an explicit NULL
//
// That second difference is one byte string fed to the KDF, therefore a different key-encryption key, and it
// is what this test caught: every algorithm matched, the structure parsed, and the unwrap failed its integrity
// check -- which reads as "this is not your key", not as a disagreement about an encoding. The opener now
// derives under both spellings, and neither fixture can be dropped without losing one of them.
//
// The producer here is BouncyCastle with the service's four named algorithms, so this fails if either side of
// that seam moves.
public class CardKeyAgreementTests
{
    private const string Aes256CbcOid = "2.16.840.1.101.3.4.1.42";

    [Fact]
    public void The_opener_agrees_the_content_key_of_an_envelope_the_service_would_send()
    {
        var plaintext = RandomNumberGenerator.GetBytes(5000);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = CardShapedCertificate(key);

        var envelope = EnvelopeWithKeyAgreement(plaintext, certificate);

        // The hardware check's own entry point, so the test exercises exactly what the card path runs — only
        // the one card operation is substituted, which is the whole reason it is a lambda.
        var opened = CardEnvelopeOpener.OpenWithAgreement(
            envelope, point => SharedSecret(point, key.ExportParameters(includePrivateParameters: true)));

        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void The_envelope_carries_the_algorithms_this_client_implements()
    {
        // The interop contract, asserted from THIS side of the seam as well — the service asserts the same
        // four OIDs from its own. Either side moving alone is a document nobody can open, and the refusals in
        // the opener name the OID rather than guessing, so a drift would surface as "an algorithm this client
        // does not implement" against a perfectly standard envelope.
        var plaintext = RandomNumberGenerator.GetBytes(64);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = CardShapedCertificate(key);

        var envelope = EnvelopeWithKeyAgreement(plaintext, certificate);
        var (point, ukm, kdfScheme, wrap) = CardEnvelopeOpener.ReadKeyAgreement(envelope);

        Assert.Equal("1.3.132.1.11.1", kdfScheme);                  // dhSinglePass-stdDH-sha256kdf-scheme
        Assert.Equal("2.16.840.1.101.3.4.1.45", wrap);              // id-aes256-wrap
        Assert.Null(ukm);                                           // none sent, so none may be fed to the KDF
        Assert.Equal(0x04, point[0]);                               // uncompressed 04||X||Y, not an SPKI
        Assert.Equal(65, point.Length);                             // P-256: 1 + 32 + 32
    }

    [Fact]
    public void A_wrong_key_fails_loudly_rather_than_producing_plausible_bytes()
    {
        // The property the whole design rests on (see KeyAgreementCrypto): RFC 3394 prepends a known
        // integrity value, so a KEK derived from the wrong agreement fails a comparison instead of yielding
        // bytes that look like a corrupt document. Worth a test because "it decrypted" is otherwise
        // indistinguishable from "it decrypted to noise" — and doubly so now that the opener tries TWO
        // encodings of the shared info: the loop must exhaust them and refuse, never return one's bytes.
        var plaintext = RandomNumberGenerator.GetBytes(64);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = CardShapedCertificate(key);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var envelope = EnvelopeWithKeyAgreement(plaintext, certificate);

        var refusal = Assert.Throws<EnvelopeNotOpenedException>(() => CardEnvelopeOpener.OpenWithAgreement(
            envelope,
            point => SharedSecret(point, wrongKey.ExportParameters(includePrivateParameters: true))));

        // The message names the exhaustion rather than one failed attempt, so a reader is not sent looking
        // for an encoding problem when the answer is that this is not their key.
        Assert.Contains("either encoding", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A P-256 certificate with <c>Key Agreement</c>, issued by a CA — the shape a YubiKey 5's slot 9D holds.
    /// </summary>
    /// <remarks>
    /// ISSUED rather than self-signed, and that is forced: .NET refuses <c>CreateSelfSigned</c> on a request
    /// whose key usage is <c>KeyAgreement</c> ("the provided key does not match the public key algorithm for
    /// this certificate"), because a key declared for agreement cannot sign. Which is also why every real
    /// certificate of this kind comes from a CA.
    /// </remarks>
    private static X509Certificate2 CardShapedCertificate(ECDsa key)
    {
        using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuerRequest = new CertificateRequest("CN=Card Test CA", issuerKey, HashAlgorithmName.SHA256);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuerRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true));
        using var issuer = issuerRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));

        var request = new CertificateRequest("CN=card@example.test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyAgreement, critical: true));
        return request.Create(
            issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1),
            RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>
    /// CMS <c>EnvelopedData</c> with one key-agreement recipient, in the encryption service's exact shape.
    /// </summary>
    /// <remarks>
    /// The four algorithms are named here for the same reason they are named there: BouncyCastle's
    /// unspecified key-agreement KDF is the SHA-1 scheme, so a default would test a structure the service
    /// does not send (and which this client happens to support, hiding the drift).
    /// </remarks>
    private static byte[] EnvelopeWithKeyAgreement(byte[] plaintext, X509Certificate2 certificate)
    {
        var recipient = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(certificate.RawData);
        var ec = (Org.BouncyCastle.Crypto.Parameters.ECPublicKeyParameters)recipient.GetPublicKey();

        var ephemeral = new Org.BouncyCastle.Crypto.Generators.ECKeyPairGenerator();
        ephemeral.Init(new Org.BouncyCastle.Crypto.Parameters.ECKeyGenerationParameters(
            ec.Parameters, new Org.BouncyCastle.Security.SecureRandom()));
        var pair = ephemeral.GenerateKeyPair();

        var generator = new Org.BouncyCastle.Cms.CmsEnvelopedDataGenerator();
        generator.AddKeyAgreementRecipient(
            Org.BouncyCastle.Cms.CmsEnvelopedGenerator.ECDHSha256Kdf,
            pair.Private,
            pair.Public,
            recipient,
            Org.BouncyCastle.Cms.CmsEnvelopedGenerator.Aes256Wrap);

        return generator.Generate(
            new Org.BouncyCastle.Cms.CmsProcessableByteArray(plaintext),
            new Org.BouncyCastle.Asn1.DerObjectIdentifier(Aes256CbcOid)).GetEncoded();
    }

    /// <summary>
    /// The one operation the card performs in production: the private half of the ECDH agreement.
    /// </summary>
    /// <remarks>
    /// <paramref name="originatorPoint"/> is the uncompressed <c>04||X||Y</c> form the CMS BIT STRING holds,
    /// which is what <c>DeriveEcdhSecret</c> hands the token — so the split here is exactly the production
    /// split, with a software key standing in for the card and nothing else changed. The RAW agreement, with
    /// no KDF of its own: the X9.63 derivation is the client's job and is what is under test.
    /// </remarks>
    private static byte[] SharedSecret(byte[] originatorPoint, ECParameters privateKey)
    {
        var coordinateLength = (originatorPoint.Length - 1) / 2;
        using var originator = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = originatorPoint[1..(1 + coordinateLength)],
                Y = originatorPoint[(1 + coordinateLength)..],
            },
        });

        using var mine = ECDiffieHellman.Create(privateKey);
        return mine.DeriveRawSecretAgreement(originator.PublicKey);
    }
}
