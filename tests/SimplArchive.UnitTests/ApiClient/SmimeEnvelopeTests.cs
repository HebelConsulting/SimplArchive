using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// The MIME steps around a strict-tier envelope (ADR 0917): what a host that opens CMS ITSELF (the phone, with its
// hardware key) needs from the shared library, exercised the way it uses them: unwrap, decrypt as bare CMS,
// read the payload's real type.
public sealed class SmimeEnvelopeTests
{
    [Fact]
    public void A_served_envelope_unwraps_to_CMS_whose_decryption_yields_the_document_and_its_real_type()
    {
        using var reader = NewCertificate();
        var pdf = "%PDF-1.7 test"u8.ToArray();
        var served = Envelope(reader, pdf, "application/pdf", "report.pdf");

        var cms = new EnvelopedCms();
        cms.Decode(SmimeEnvelope.Cms(served));
        cms.Decrypt(cms.RecipientInfos[0], new X509Certificate2Collection(reader));
        var (bytes, contentType) = SmimeEnvelope.Payload(cms.ContentInfo.Content);

        Assert.Equal(pdf, bytes);
        Assert.Equal("application/pdf", contentType);
    }

    [Fact]
    public void A_response_labelled_as_an_envelope_that_holds_none_is_refused()
    {
        var plain = "Content-Type: text/plain\r\n\r\nhello"u8.ToArray();

        Assert.Throws<EnvelopeNotOpenedException>(() => SmimeEnvelope.Unwrap(plain));
    }

    [Fact]
    public void A_decrypted_entity_that_is_not_a_document_is_refused()
    {
        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = "a" } };

        Assert.Throws<EnvelopeNotOpenedException>(() => SmimeEnvelope.Payload(multipart));
    }

    private static byte[] Envelope(X509Certificate2 recipient, byte[] content, string contentType, string fileName)
    {
        var message = new MimeMessage
        {
            Body = new MimePart(ContentType.Parse(contentType))
            {
                Content = new MimeContent(new MemoryStream(content)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };
        using var context = new TemporarySecureMimeContext();
        message.Body = ApplicationPkcs7Mime.Encrypt(
            context,
            new MimeKit.Cryptography.CmsRecipientCollection { new MimeKit.Cryptography.CmsRecipient(recipient) { EncryptionAlgorithms = [EncryptionAlgorithm.Aes256] } },
            message.Body);
        using var output = new MemoryStream();
        message.WriteTo(output);
        return output.ToArray();
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=reader-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment, critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }
}
