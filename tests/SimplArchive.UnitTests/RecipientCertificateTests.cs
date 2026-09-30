using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.Api.Documents;

namespace SimplArchive.UnitTests;

// Which certificates the enveloping tiers will address (#1498, ADRs 0827/0842/0863).
//
// WHY THIS EXISTS. `Validate` accepted RSA keys ONLY, refusing anything else with "S/MIME key transport needs
// an RSA key" — a premise that had stopped being true. CMS addresses an EC recipient by KEY AGREEMENT, the
// encryption service produces such an envelope (its ADR 0018) and the desktop client opens one with the
// agreement performed on the card (ADR 0830). A PIV card's key-management slot normally holds an EC key, so
// the one certificate kind the product exists to serve was the one this refused.
//
// AND IT FAILED SILENTLY, in the direction that hides it. `StrictEnvelopeDelivery.Usable` swallows the
// refusal on purpose — an unreadable certificate should make the rel disappear rather than fail on click — so
// the reader's certificate set came back empty, the strict tier's `download`/`preview` rels fell back to a
// presigned storage URL, and the desktop fetched bytes it could not recognise. Measured on the demo stack:
// "Preview not supported", and the card was never asked for its PIN, because no envelope was ever built.
public class RecipientCertificateTests
{
    [Fact]
    public void An_rsa_certificate_is_accepted()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=laptop@example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var described = RecipientCertificate.Validate(Pem(certificate));

        Assert.Equal(certificate.GetCertHashString(HashAlgorithmName.SHA256), described.Fingerprint);
        Assert.Contains("laptop@example.test", described.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ec_card_certificate_is_accepted()
    {
        // The shape a YubiKey 5 issues in slot 9D: P-256 with Key Agreement, ISSUED by a CA — .NET refuses
        // to self-sign a KeyAgreement-only request, because a key declared for agreement cannot sign, which
        // is also why every real certificate of this kind comes from a CA.
        using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuerRequest = new CertificateRequest("CN=Card Test CA", issuerKey, HashAlgorithmName.SHA256);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuerRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true));
        using var issuer = issuerRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=card@example.test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyAgreement, critical: true));
        using var certificate = request.Create(
            issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1),
            RandomNumberGenerator.GetBytes(16));

        var described = RecipientCertificate.Validate(Pem(certificate));

        Assert.Equal(certificate.GetCertHashString(HashAlgorithmName.SHA256), described.Fingerprint);
    }

    [Fact]
    public void Something_that_is_not_a_certificate_is_refused()
    {
        var refusal = Assert.Throws<Api.Errors.Exceptions.ExternalLinks.InvalidRecipientCertificateException>(
            () => RecipientCertificate.Validate("-----BEGIN CERTIFICATE-----\nnot base64\n-----END CERTIFICATE-----"));

        // The message is shown to whoever pasted it, so it must name the input rather than the internals.
        Assert.Contains("PEM", refusal.Message, StringComparison.Ordinal);
    }

    private static string Pem(X509Certificate2 certificate) =>
        new(PemEncoding.Write("CERTIFICATE", certificate.RawData));
}
