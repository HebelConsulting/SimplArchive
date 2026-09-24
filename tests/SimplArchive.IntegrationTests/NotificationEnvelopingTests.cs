using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using SimplArchive.Infrastructure.Notifications;

namespace SimplArchive.IntegrationTests;

// The notification envelope seam (#1334): what SmtpEmailSender wraps, the recipient's stack must open —
// cross-implementation like every S/MIME test in this codebase (MimeKit envelopes, .NET's EnvelopedCms
// decrypts), because a test that reuses the producer's library to consume proves the library, not the wire.
public class NotificationEnvelopingTests
{
    private static (X509Certificate2 WithKey, string Pem) Recipient()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=notify@example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate, certificate.ExportCertificatePem());
    }

    private static MimeMessage Message(string subject, string body)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("SimplArchive", "noreply@example.test"));
        message.To.Add(new MailboxAddress("N", "notify@example.test"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    [Fact]
    public void The_enveloped_body_opens_with_the_recipients_key_and_the_subject_stays_as_given()
    {
        var (withKey, pem) = Recipient();
        using var _ = withKey;
        var message = Message("SimplArchive — new notification", "Approve 'Salary review 2026' by Friday.");

        SmtpEmailSender.TryEnvelopeBody(message, pem, NullLogger.Instance);

        var enveloped = Assert.IsType<MimeKit.Cryptography.ApplicationPkcs7Mime>(message.Body);
        Assert.Equal("SimplArchive — new notification", message.Subject); // headers cannot encrypt — by design

        using var raw = new MemoryStream();
        enveloped.Content!.DecodeTo(raw);
        var cms = new EnvelopedCms();
        cms.Decode(raw.ToArray());
        cms.Decrypt(new X509Certificate2Collection(withKey));
        Assert.Contains("Salary review 2026", System.Text.Encoding.UTF8.GetString(cms.ContentInfo.Content), StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_certificate_fails_open_to_plaintext()
    {
        var message = Message("T", "B");
        SmtpEmailSender.TryEnvelopeBody(message, "-----BEGIN CERTIFICATE-----\nnot a cert\n-----END CERTIFICATE-----", NullLogger.Instance);
        Assert.IsType<TextPart>(message.Body); // untouched — the notification still goes out
    }
}
