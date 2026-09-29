using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Encryption;

namespace SimplArchive.UnitTests;

// One envelope, several readers' keys (ADR 0842, #1424).
//
// This is the assertion the ADR leans on rather than a restatement of it. The claim that dissolved the
// "which certificate wins?" question is that a CMS EnvelopedData addresses as many recipients as you like —
// each getting the content-encryption key wrapped to their own public key over ONE copy of the ciphertext —
// so a reader opens it with whichever key they have to hand and nothing has to choose for them.
//
// A test that merely counted RecipientInfos would pass on an envelope nobody could open. So these DECRYPT,
// with two DIFFERENT private keys, and compare the plaintext back.
public class SmimeSeveralRecipientsTests
{
    [Fact]
    public void One_envelope_opens_with_either_readers_private_key()
    {
        var enveloper = new SmimeMessageEnveloper(NullLogger<SmimeMessageEnveloper>.Instance);
        var card = SelfSigned("CN=reader card");
        var laptop = SelfSigned("CN=reader laptop");
        var content = System.Text.Encoding.UTF8.GetBytes("the document's bytes");

        var enveloped = enveloper.TryEnvelopeDocument(
            content, "text/plain", "report.txt", from: null,
            [card.CertificatePem, laptop.CertificatePem]);

        Assert.NotNull(enveloped);
        var cms = CmsBytesOf(enveloped!);

        // Both keys are addressed...
        var addressed = new EnvelopedCms();
        addressed.Decode(cms);
        Assert.Equal(2, addressed.RecipientInfos.Count);

        // ...and each one ACTUALLY OPENS IT. The card and the laptop are the same person at two desks.
        // Decoded FRESH from the original bytes per holder: EnvelopedCms.Encode() after a Decode does not
        // round-trip, so re-encoding here would test the test rather than the envelope.
        foreach (var holder in new[] { card, laptop })
        {
            var opened = new EnvelopedCms();
            opened.Decode(cms);
            opened.Decrypt(new X509Certificate2Collection(holder.WithKey));

            // The decrypted payload is the inner MIME part, so the DOCUMENT's bytes come back through its
            // transfer encoding. Comparing the bytes rather than a substring is what proves the whole round
            // trip: wrapped, addressed twice, opened with one key, and byte-identical to what went in.
            Assert.Equal(content, DecodedDocument(opened.ContentInfo.Content));
        }
    }

    [Fact]
    public void A_reader_whose_key_was_not_addressed_cannot_open_it()
    {
        // The other half: several recipients must not mean "anyone". Without this, a change that addressed
        // every enrolled certificate in the TENANT rather than this reader's would still pass the test above.
        var enveloper = new SmimeMessageEnveloper(NullLogger<SmimeMessageEnveloper>.Instance);
        var addressed = SelfSigned("CN=addressed");
        var stranger = SelfSigned("CN=stranger");

        var enveloped = enveloper.TryEnvelopeDocument(
            System.Text.Encoding.UTF8.GetBytes("secret"), "text/plain", "s.txt", from: null,
            [addressed.CertificatePem]);

        var opened = new EnvelopedCms();
        opened.Decode(CmsBytesOf(enveloped!));

        Assert.ThrowsAny<CryptographicException>(
            () => opened.Decrypt(new X509Certificate2Collection(stranger.WithKey)));
    }

    [Fact]
    public void No_certificates_is_refused_rather_than_enveloped_to_nobody()
    {
        // An envelope addressed to nobody would decode, and would be openable by nobody — a document that
        // left the building and is lost. Null here is the caller's cue to REFUSE (ADR 0825).
        var enveloper = new SmimeMessageEnveloper(NullLogger<SmimeMessageEnveloper>.Instance);

        Assert.Null(enveloper.TryEnvelopeDocument(
            [1, 2, 3], "text/plain", "x.txt", from: null, []));
    }

    /// <summary>The document's own bytes, out of the decrypted inner MIME part.</summary>
    private static byte[] DecodedDocument(byte[] innerMime)
    {
        var entity = MimeKit.MimeEntity.Load(new MemoryStream(innerMime));
        var part = Assert.IsAssignableFrom<MimeKit.MimePart>(entity);
        Assert.NotNull(part.Content);
        using var decoded = new MemoryStream();
        part.Content!.DecodeTo(decoded);
        return decoded.ToArray();
    }

    /// <summary>The raw CMS bytes out of the S/MIME message the enveloper produced.</summary>
    private static byte[] CmsBytesOf(byte[] rfc822)
    {
        var message = MimeKit.MimeMessage.Load(new MemoryStream(rfc822));
        var part = Assert.IsAssignableFrom<MimeKit.MimePart>(message.Body);
        Assert.NotNull(part.Content);
        using var raw = new MemoryStream();
        part.Content!.DecodeTo(raw);

        return raw.ToArray();
    }

    private static (string CertificatePem, X509Certificate2 WithKey) SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate.ExportCertificatePem(), certificate);
    }
}
