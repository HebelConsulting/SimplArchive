using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Encryption;

namespace SimplArchive.UnitTests;

// CAN THE SERVER ADDRESS AN ELLIPTIC-CURVE READER? Asked of the real enveloper, because the answer decides
// whether EC certificates are usable end to end or only readable.
//
// It is not obvious from the code: .NET's own EnvelopedCms CANNOT create an EC recipient on Unix — its managed
// PAL throws "Unknown algorithm 1.2.840.10045.2.1" — but this enveloper goes through MimeKit, which is
// BouncyCastle-backed and may well manage it. Reasoning about which applies is exactly how a wrong conclusion
// gets written down, so this asks.
public class SmimeEcRecipientProbeTests
{
    [Fact]
    public void An_EC_certificate_can_or_cannot_be_addressed_and_this_records_which()
    {
        var enveloper = new SmimeMessageEnveloper(NullLogger<SmimeMessageEnveloper>.Instance);
        var content = System.Text.Encoding.UTF8.GetBytes("the document's bytes");

        var enveloped = enveloper.TryEnvelopeDocument(
            content, "text/plain", "report.txt", from: null, [EcCertificatePem()]);

        // Asserted rather than merely printed: if MimeKit gains or loses the ability, the build says so on the
        // day it changes rather than in whatever code next assumed the old answer.
        Assert.NotNull(enveloped);
    }

    private static string EcCertificatePem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=crypt@demo.simplarchive.dev", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyAgreement, critical: true));

        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using var certificate = request.Create(
            request.SubjectName, X509SignatureGenerator.CreateForECDsa(key),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);

        return certificate.ExportCertificatePem();
    }
}
