using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;

namespace SimplArchive.Infrastructure.Encryption;

/// <summary>
/// Envelopes a built MIME message's body to the user's own stored certificate (#1332) — the in-process
/// sibling of <see cref="MessageEnvelopeClient"/>: same CMS EnvelopedData shape, no sidecar, because
/// enveloping to a known certificate is software crypto and the encryption service exists for the HSM
/// half, not this. The top-level headers stay readable (standard S/MIME); the body becomes
/// <c>application/pkcs7-mime</c>.
/// </summary>
public sealed class SmimeMessageEnveloper(ILogger<SmimeMessageEnveloper> logger)
{
    /// <summary>
    /// Returns the enveloped RFC-822 bytes, or null when the stored certificate cannot be used — the
    /// caller serves plaintext then, the same fail-open contract as the sidecar client's network failures
    /// (milestone 1's stated boundary). The Warning names the switch: PUT a fresh certificate, or delete it.
    /// </summary>
    public byte[]? TryEnvelope(byte[] rfc822, string certificatePem, string email) =>
        TryEnvelope(MimeMessage.Load(new MemoryStream(rfc822)), certificatePem, email);

    /// <summary>
    /// Envelopes a document's bytes as an S/MIME message addressed to a certificate (#1377, ADR 0827) — what a
    /// strict-tier external link serves instead of a presigned URL to plaintext.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A MESSAGE rather than a bare CMS blob, and that is the decision rather than an implementation detail:
    /// the recipient is an outsider with no client of ours, and a `.p7m` message opens by double-click in
    /// S/MIME-capable mail software, carrying the filename and content type in headers that survive the
    /// envelope. A bare `EnvelopedData` would need `openssl cms -decrypt` and a person who knows to run it.
    /// </para>
    /// <para>
    /// <b>The headers stay readable</b> — that is how S/MIME works, and it means the document's NAME travels in
    /// clear inside the artefact. Deliberate: the name is metadata, and ADR 0825 already accepts plaintext
    /// metadata as the tier's price. Removing it would cost the recipient the one clue telling them which
    /// document they were sent.
    /// </para>
    /// </remarks>
    public byte[]? TryEnvelopeDocument(
        byte[] content, string contentType, string fileName, string? from, string certificatePem)
    {
        MimeMessage message;
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            message = new MimeMessage
            {
                Subject = Path.GetFileNameWithoutExtension(fileName),
                Body = new MimePart(ContentType.Parse(contentType))
                {
                    Content = new MimeContent(new MemoryStream(content)),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
                    ContentTransferEncoding = ContentEncoding.Base64,
                },
            };

            // Addresses are a courtesy to the reader's mail client, not part of the security story — the
            // envelope is what addresses this, and it addresses a KEY. Both are omitted rather than invented
            // when unknown: a From nobody sent from, or a To nobody can reply to, is worse than a blank field.
            if (from is { Length: > 0 })
            {
                message.From.Add(new MailboxAddress(from, from));
            }

            if (certificate.GetNameInfo(X509NameType.EmailName, forIssuer: false) is { Length: > 0 } recipient)
            {
                message.To.Add(new MailboxAddress(recipient, recipient));
            }
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or ArgumentException or FormatException)
        {
            logger.LogWarning(exception, "A recipient certificate could not be read, so {FileName} was not enveloped.", fileName);
            return null;
        }

        return TryEnvelope(message, certificatePem, fileName);
    }

    /// <summary>
    /// The ONE enveloping implementation. Returns null when the certificate cannot be used.
    /// </summary>
    /// <remarks>
    /// <b>Null means "could not", and what to do about it is the CALLER's policy, not this method's.</b> The
    /// IMAP funnel serves plaintext (milestone 1's stated fail-open boundary); a strict-tier external link
    /// REFUSES, because falling back to plaintext there is exactly what ADR 0825 forbids. Both read the same
    /// null and answer differently, which is why this stays neutral rather than throwing or defaulting.
    /// </remarks>
    private byte[]? TryEnvelope(MimeMessage message, string certificatePem, string describedAs)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            if (message.Body is not { } body)
            {
                return null; // a degenerate message with no body — nothing to envelope
            }

            using var context = new TemporarySecureMimeContext();
            // SmimeRecipient, not `new CmsRecipient(certificate)`: the latter states no cipher preference, and
            // MimeKit then falls back to 3DES because a bare certificate advertises no S/MIME capabilities.
            message.Body = ApplicationPkcs7Mime.Encrypt(
                context, new CmsRecipientCollection { SmimeRecipient.For(certificate) }, body);

            using var output = new MemoryStream();
            message.WriteTo(output);
            return output.ToArray();
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or ArgumentException or FormatException)
        {
            logger.LogWarning(exception,
                "The S/MIME certificate for {DescribedAs} could not envelope a message. " +
                "Re-upload or delete the certificate.", describedAs);
            return null;
        }
    }
}
