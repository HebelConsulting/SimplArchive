using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop opens the envelope a strict-tier read arrives in (#1353, ADR 0828).
//
// This is the issue's acceptance step ONE, and its order is the point: prove decryption against a SOFTWARE
// certificate before involving a card. Doing the card first means a failure has two candidate causes — "can
// .NET decrypt at all" and "can .NET drive a token-backed key" — with no way to separate them.
//
// Built against a real envelope produced the way the server produces one (MimeKit, CMS EnvelopedData, RSA key
// transport), rather than a fixture blob: a test that constructs its own idea of the format proves the client
// agrees with the TEST, which is the failure mode of every replay built by analogy.
[Collection(UiCollection.Name)]
public class DesktopEnvelopeOpenerTests : IDisposable
{
    private const string Marker = "DESKTOP-OPENED-THE-ENVELOPE";

    private readonly Func<X509Certificate2Collection> _originalKeys = EnvelopeOpener.Keys;
    private readonly IReadOnlyList<EnvelopeOpener.Opener> _originalOpeners = EnvelopeOpener.Openers;

    public void Dispose()
    {
        EnvelopeOpener.Keys = _originalKeys;
        EnvelopeOpener.Openers = _originalOpeners;
    }

    [Fact]
    public async Task An_envelope_addressed_to_a_key_this_machine_holds_opens_into_the_document()
    {
        var recipient = NewCertificate();
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [recipient];

        var (bytes, contentType) = await EnvelopeOpener.OpenAsync(
            Envelope(recipient, Encoding.ASCII.GetBytes(Marker), "application/pdf", "invoice.pdf"),
            "application/pkcs7-mime");

        Assert.Equal(Marker, Encoding.ASCII.GetString(bytes));

        // The INNER type, not the wrapper's: a PDF announced as pkcs7-mime would be sniffed, mis-rendered or
        // refused by every consumer downstream of this funnel.
        Assert.Equal("application/pdf", contentType);
    }

    [Fact]
    public async Task An_ordinary_response_is_returned_exactly_as_it_arrived()
    {
        // The anti-vacuous half, and the one that matters for every tenant that is NOT strict: this hook sits
        // in the funnel every read passes through, so "does nothing unless it is an envelope" is a property of
        // the whole client rather than of this method.
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [];
        var plain = Encoding.ASCII.GetBytes("%PDF-1.7 an ordinary document");

        var (bytes, contentType) = await EnvelopeOpener.OpenAsync(plain, "application/pdf");

        Assert.Same(plain, bytes);
        Assert.Equal("application/pdf", contentType);
    }

    [Fact]
    public async Task An_envelope_addressed_to_somebody_else_refuses_rather_than_returning_the_bytes()
    {
        // Never a fall back to what arrived. Handing the caller undecrypted CMS would render as a corrupt
        // document and send them looking for a damaged file instead of a missing key.
        var addressee = NewCertificate();
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [NewCertificate()];

        var thrown = await Assert.ThrowsAsync<EnvelopeNotOpenedException>(() => EnvelopeOpener.OpenAsync(
            Envelope(addressee, Encoding.ASCII.GetBytes(Marker), "application/pdf", "invoice.pdf"),
            "application/pkcs7-mime"));

        // The message now names which of the three things is missing (#1353 acceptance 3), and this host has
        // no card in a reader — so it must say so rather than blaming the document.
        Assert.Contains("addressed to", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_key_at_all_it_says_that_rather_than_that_the_document_is_wrong()
    {
        // The commonest real case — a reader who has not enrolled — and the message decides whether they go
        // looking for their certificate or for a broken file.
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [];

        var thrown = await Assert.ThrowsAsync<EnvelopeNotOpenedException>(() => EnvelopeOpener.OpenAsync(
            Envelope(NewCertificate(), [1, 2, 3], "application/pdf", "x.pdf"), "application/pkcs7-mime"));

        Assert.Contains("addressed to", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the software opener, so these tests are about decryption rather than about this host's hardware.
    /// </summary>
    /// <remarks>
    /// Without it, the card opener would run on a developer's machine with a reader and not on CI, which makes
    /// the same assertion mean two different things — the shape that produces a test nobody trusts.
    /// </remarks>
    private static void SoftwareOnly() =>
        EnvelopeOpener.Openers = [EnvelopeOpener.Openers[0]];

    /// <summary>An envelope built the way the server builds one — same library, same shape.</summary>
    private static byte[] Envelope(X509Certificate2 recipient, byte[] content, string contentType, string fileName)
    {
        var message = new MimeMessage
        {
            Subject = Path.GetFileNameWithoutExtension(fileName),
            Body = new MimePart(ContentType.Parse(contentType))
            {
                Content = new MimeContent(new MemoryStream(content)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };

        // AES-256 STATED EXPLICITLY, because `new CmsRecipient(certificate)` alone does not get it: a bare
        // certificate advertises no S/MIME capabilities and MimeKit then falls back to 3DES. A fixture built
        // the easy way would have proven this client opens a cipher the server never sends.
        //
        // The server's own SmimeRecipient.For states the same preference, and SmimeContentCipherTests pins it
        // by decoding a real envelope's OID rather than by reading the list — so if production's cipher ever
        // moves, that test fails and names the change, and this fixture follows it. Duplicated rather than
        // referenced because linking Infrastructure here drags EF and Npgsql into a desktop test project and
        // collides with its own pins: a one-line duplicate with an anchor beats that trade.
        using var context = new TemporarySecureMimeContext();
        message.Body = ApplicationPkcs7Mime.Encrypt(
            context,
            new CmsRecipientCollection
            {
                new CmsRecipient(recipient) { EncryptionAlgorithms = [EncryptionAlgorithm.Aes256] },
            },
            message.Body);

        using var output = new MemoryStream();
        message.WriteTo(output);
        return output.ToArray();
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=reader-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Round-tripped through PKCS#12 so the instance carries a usable private key on every platform —
        // CreateSelfSigned's handle is not always one MimeKit can import.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "t"), "t",
            X509KeyStorageFlags.Exportable);
    }
}
