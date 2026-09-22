using System.Security.Cryptography.X509Certificates;
using MimeKit;
using MimeKit.Cryptography;

namespace SimplArchive.Api.Encryption;

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
    public byte[]? TryEnvelope(byte[] rfc822, string certificatePem, string email)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            var message = MimeMessage.Load(new MemoryStream(rfc822));
            if (message.Body is not { } body)
            {
                return null; // a degenerate message with no body — nothing to envelope
            }

            using var context = new TemporarySecureMimeContext();
            message.Body = ApplicationPkcs7Mime.Encrypt(
                context, new CmsRecipientCollection { new CmsRecipient(certificate) }, body);

            using var output = new MemoryStream();
            message.WriteTo(output);
            return output.ToArray();
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or ArgumentException or FormatException)
        {
            logger.LogWarning(exception,
                "The stored S/MIME certificate for {Email} could not envelope a message — serving plaintext. " +
                "Re-upload or delete the certificate in the profile dialog.", email);
            return null;
        }
    }
}
