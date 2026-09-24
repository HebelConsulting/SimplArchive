using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Encryption;

namespace SimplArchive.UnitTests;

// What cipher an S/MIME envelope actually uses, read off the produced bytes.
//
// WHY THIS EXISTS. MimeKit picks the strongest algorithm the recipient's S/MIME capabilities advertise, and
// falls back to 3DES when it knows none — which is always here, because this codebase envelopes to a bare
// certificate that carries no capabilities attribute. So `new CmsRecipient(certificate)` quietly produced
// des-EDE3-CBC on every IMAP FETCH and every notification mail.
//
// Nothing could tell. 3DES is a WORKING cipher: every envelope decrypted correctly, every test passed, and
// the weakness is only visible in an algorithm identifier nobody was reading. It was found while proving
// something else entirely — decrypting a server envelope with a smartcard (#1353) — when the unwrapped
// content-encryption key came back 24 bytes instead of 32.
//
// So the assertion is on the OID in the CMS, not on the configuration that ought to produce it: a test that
// checked `EncryptionAlgorithms` contains Aes256 would pass even if MimeKit ignored the list.
public class SmimeContentCipherTests
{
    private const string Aes256Cbc = "2.16.840.1.101.3.4.1.42";
    private const string TripleDesCbc = "1.2.840.113549.3.7";

    [Fact]
    public void An_enveloped_message_uses_aes_256_rather_than_the_library_default()
    {
        var enveloper = new SmimeMessageEnveloper(NullLogger<SmimeMessageEnveloper>.Instance);
        var (certificatePem, _) = SelfSigned();

        var rfc822 = """
            From: archive@example.com
            To: reader@example.com
            Subject: cipher check

            the body
            """.ReplaceLineEndings("\r\n");

        var enveloped = enveloper.TryEnvelope(System.Text.Encoding.ASCII.GetBytes(rfc822), certificatePem, "reader@example.com");
        Assert.NotNull(enveloped);

        var algorithm = ContentAlgorithmOf(enveloped!);

        Assert.True(algorithm == Aes256Cbc,
            $"the envelope's content cipher is {algorithm}, not AES-256-CBC ({Aes256Cbc}). "
            + (algorithm == TripleDesCbc
                ? "That is 3DES — MimeKit's fallback when the recipient states no preference, which means a "
                  + "CmsRecipient was built without going through SmimeRecipient.For()."
                : "Something changed the stated cipher preference."));
    }

    /// <summary>The content-encryption algorithm OID inside an S/MIME message's application/pkcs7-mime part.</summary>
    /// <remarks>
    /// Walks to the CMS and reads the algorithm identifier directly. Deliberately not via a decrypt: the
    /// question is which cipher was CHOSEN, and a successful decrypt answers a different one.
    /// </remarks>
    private static string ContentAlgorithmOf(byte[] rfc822)
    {
        var message = MimeKit.MimeMessage.Load(new MemoryStream(rfc822));
        Assert.NotNull(message.Body);
        var part = Assert.IsAssignableFrom<MimeKit.MimePart>(message.Body);

        Assert.NotNull(part.Content);
        using var content = new MemoryStream();
        part.Content.DecodeTo(content);

        // ContentInfo ::= SEQUENCE { contentType OID, [0] EXPLICIT EnvelopedData }
        var outer = new AsnReader(content.ToArray(), AsnEncodingRules.BER).ReadSequence();
        outer.ReadObjectIdentifier();
        var envelopedData = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();

        envelopedData.ReadInteger();
        if (envelopedData.PeekTag().TagClass == TagClass.ContextSpecific)
        {
            envelopedData.ReadEncodedValue(); // originatorInfo [0]
        }

        envelopedData.ReadSetOf();            // recipientInfos
        var encryptedContentInfo = envelopedData.ReadSequence();
        encryptedContentInfo.ReadObjectIdentifier();
        return encryptedContentInfo.ReadSequence().ReadObjectIdentifier();
    }

    private static (string CertificatePem, RSA Key) SelfSigned()
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=cipher check", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return (certificate.ExportCertificatePem(), key);
    }
}
