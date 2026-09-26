using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// An envelope says who it is addressed to, and every opener asks before it reaches for a key (#1353, ADR 0832).
//
// WHY THIS IS NOT AN OPTIMISATION, which is the thing a later reader will assume and then "simplify" away.
// Both openers used to take a key first and find out afterwards, and both paid for it in a credential prompt
// the user had no reason to see:
//
//   - the software opener imported every certificate in the platform store, and MimeKit needs the PRIVATE KEY
//     to build a decryptor — so on macOS a document addressed to a CARD raised keychain authorization prompts
//     for unrelated software certificates before the card was ever consulted. Observed, not theorised: one of
//     those imports failed with "Unable to obtain authorization for this operation" and took the whole read
//     down with it;
//   - the card opener opened a logged-in session, which means a PIN PROMPT, before establishing that the card
//     was the addressee at all.
//
// A prompt whose answer is going to be "no" is how people learn to click through prompts. So the negative
// tests below are the valuable ones.
public class DesktopEnvelopeRecipientTests
{
    [Fact]
    public void An_envelope_names_its_recipient_by_issuer_and_serial()
    {
        var recipient = NewCertificate();

        var named = EnvelopeRecipients.Of(Envelope(recipient));

        var one = Assert.Single(named);
        Assert.Equal(recipient.SerialNumber, one.Serial, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(recipient.Issuer, one.Issuer);
    }

    [Fact]
    public void A_certificate_that_is_the_addressee_matches()
    {
        var recipient = NewCertificate();

        Assert.True(EnvelopeRecipients.Matches(recipient, EnvelopeRecipients.Of(Envelope(recipient))));
    }

    [Fact]
    public void SOMEBODY_ELSES_certificate_does_not_match_and_so_is_never_unlocked()
    {
        // The case that removes the prompt: this certificate cannot open the document, and now we know that
        // WITHOUT asking the platform for its private key.
        var addressee = NewCertificate();
        var mine = NewCertificate();

        Assert.False(EnvelopeRecipients.Matches(mine, EnvelopeRecipients.Of(Envelope(addressee))));
    }

    [Fact]
    public void Only_the_addressed_certificate_is_selected_out_of_a_full_store()
    {
        // A realistic store: several identities, one of which is the recipient. Only that one may be handed to
        // MimeKit, because handing over any other is what prompts.
        var addressee = NewCertificate();
        var store = new X509Certificate2Collection { NewCertificate(), addressee, NewCertificate() };

        var chosen = EnvelopeRecipients.AddressedTo(EnvelopeRecipients.Of(Envelope(addressee)), store);

        var one = Assert.Single(chosen);
        Assert.Equal(addressee.SerialNumber, one.SerialNumber);
    }

    [Fact]
    public void A_store_holding_none_of_the_recipients_yields_nothing_to_unlock()
    {
        // Precisely the card case: the document is addressed to a key on a token, and the software store holds
        // three certificates that are not it. None may be touched.
        var store = new X509Certificate2Collection { NewCertificate(), NewCertificate(), NewCertificate() };

        Assert.Empty(EnvelopeRecipients.AddressedTo(EnvelopeRecipients.Of(Envelope(NewCertificate())), store));
    }

    [Fact]
    public void Bytes_that_are_not_an_envelope_name_nobody_rather_than_throwing()
    {
        // Unreadable here must be quiet: the opener that owns the envelope refuses with its own message, which
        // is better placed to say what went wrong than a helper that only reads addresses.
        var notAnEnvelope = new ApplicationPkcs7Mime(
            SecureMimeType.EnvelopedData, new MemoryStream(Encoding.ASCII.GetBytes("nonsense")));

        Assert.Empty(EnvelopeRecipients.Of(notAnEnvelope));
    }

    private static ApplicationPkcs7Mime Envelope(X509Certificate2 recipient)
    {
        var body = new MimePart(ContentType.Parse("application/pdf"))
        {
            Content = new MimeContent(new MemoryStream("content"u8.ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "x.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };

        using var context = new TemporarySecureMimeContext();
        return (ApplicationPkcs7Mime)ApplicationPkcs7Mime.Encrypt(
            context,
            new CmsRecipientCollection
            {
                new CmsRecipient(recipient) { EncryptionAlgorithms = [EncryptionAlgorithm.Aes256] },
            },
            body);
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=holder-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12, "t"), "t", X509KeyStorageFlags.Exportable);
    }
}
