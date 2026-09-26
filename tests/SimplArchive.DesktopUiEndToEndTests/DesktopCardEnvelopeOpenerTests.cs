using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The card's half of opening an envelope (#1353, ADR 0832).
//
// No test has a card, so the on-card unwrap itself is proven by `--card-open-test` against real hardware and by
// nothing else. What IS testable here are the two pieces that would break reading silently if they drifted, and
// both are worth pinning precisely because they look like plumbing:
//
//   - the CONTENT CIPHER AND IV, read out of the CMS structure. .NET's EnvelopedCms reports empty algorithm
//     parameters for AES-CBC, so the IV has to come from the DER; the first working version found it by
//     scanning for the AES OID and stepping two bytes, which happens to work and would fail the first time
//     anything about the encoding moved. These tests read a REAL envelope, built the way the server builds one.
//   - the REFUSAL MESSAGE, which #1353 makes an acceptance criterion: one generic failure sends three different
//     people to three wrong places.
public class DesktopCardEnvelopeOpenerTests
{
    private const string AesCbc256 = "2.16.840.1.101.3.4.1.42";

    [Fact]
    public void The_content_cipher_and_IV_are_read_out_of_a_real_envelope()
    {
        var (cms, _) = ServerShapedEnvelope();

        var (cipherOid, iv) = CardEnvelopeOpener.ReadContentCipher(cms);

        Assert.Equal(AesCbc256, cipherOid);

        // 16 bytes is AES's block size, and the number the card path then feeds to Aes.IV. A parser that
        // silently returned something shorter would produce plausible-looking garbage rather than an error.
        Assert.Equal(16, iv.Length);
    }

    [Fact]
    public void The_IV_read_from_the_structure_is_the_one_that_actually_decrypts()
    {
        // The anti-vacuous half, and the whole point: a parser can return 16 bytes from the wrong place. This
        // decrypts the envelope with the software key, takes the content key, and checks that the PARSED IV
        // reproduces the same plaintext — which is exactly what the card path does after the token hands back
        // the key, minus the token.
        var recipient = NewCertificate();
        var (cms, marker) = ServerShapedEnvelope(recipient);

        var envelopedCms = new System.Security.Cryptography.Pkcs.EnvelopedCms();
        envelopedCms.Decode(cms);

        var contentKey = recipient.GetRSAPrivateKey()!.Decrypt(
            envelopedCms.RecipientInfos[0].EncryptedKey, RSAEncryptionPadding.Pkcs1);

        var (_, iv) = CardEnvelopeOpener.ReadContentCipher(cms);

        using var aes = Aes.Create();
        aes.Key = contentKey;
        aes.IV = iv;
        var inner = aes.CreateDecryptor().TransformFinalBlock(
            envelopedCms.ContentInfo.Content, 0, envelopedCms.ContentInfo.Content.Length);

        var part = Assert.IsAssignableFrom<MimePart>(MimeEntity.Load(new MemoryStream(inner)));
        using var plaintext = new MemoryStream();
        part.Content!.DecodeTo(plaintext);

        Assert.Equal(marker, Encoding.ASCII.GetString(plaintext.ToArray()));
        Assert.Equal("application/pdf", part.ContentType.MimeType);
    }

    [Fact]
    public void Bytes_that_are_not_a_CMS_structure_refuse_with_a_reason()
    {
        var thrown = Assert.Throws<EnvelopeNotOpenedException>(
            () => CardEnvelopeOpener.ReadContentCipher(Encoding.ASCII.GetBytes("not an envelope at all")));

        Assert.Contains("could not be read", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "no smartcard software")]
    [InlineData(true, false, "no card or token is in a reader")]
    [InlineData(true, true, "neither this computer nor the card")]
    public void The_refusal_names_which_of_the_three_things_is_missing(
        bool smartcardSoftware, bool cardPresent, string expected)
    {
        // #1353 acceptance 3. Pure over what was found, precisely so every branch is checkable on a machine
        // that has none of them — a test over the real probe would assert whatever this host happens to own.
        var message = CardEnvelopeOpener.Missing(smartcardSoftware, cardPresent);

        Assert.Contains(expected, message, StringComparison.Ordinal);

        // And every branch must still say what the actual problem is, or a user reads "no card in a reader" and
        // goes looking for a reader when their certificate was simply never registered.
        Assert.Contains("addressed to", message, StringComparison.Ordinal);
    }

    /// <summary>An envelope built exactly the way the server builds one — MimeKit, CMS, AES-256 stated.</summary>
    private static (byte[] Cms, string Marker) ServerShapedEnvelope(X509Certificate2? recipient = null)
    {
        recipient ??= NewCertificate();
        var marker = $"CARD-PATH-{Guid.NewGuid():N}";

        var message = new MimeMessage
        {
            Subject = "invoice",
            Body = new MimePart(ContentType.Parse("application/pdf"))
            {
                Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(marker))),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "invoice.pdf" },
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };

        // AES-256 stated EXPLICITLY, because a bare CmsRecipient advertises no S/MIME capabilities and MimeKit
        // then falls back to 3DES — which would make this a test of a cipher the server never sends (#1372).
        using var context = new TemporarySecureMimeContext();
        var enveloped = (ApplicationPkcs7Mime)ApplicationPkcs7Mime.Encrypt(
            context,
            new CmsRecipientCollection
            {
                new CmsRecipient(recipient) { EncryptionAlgorithms = [EncryptionAlgorithm.Aes256] },
            },
            message.Body);

        using var cms = new MemoryStream();
        enveloped.Content!.DecodeTo(cms);
        return (cms.ToArray(), marker);
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=card-reader-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12, "t"), "t", X509KeyStorageFlags.Exportable);
    }
}
