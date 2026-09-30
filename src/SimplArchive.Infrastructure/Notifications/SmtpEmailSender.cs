using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Notifications;

// Sends a plain-text email over SMTP via MailKit (ADR "Email notifications (SMTP)"). Registered only when an
// SMTP host is configured; a new connection per message (fine at notification volume). A send failure throws,
// so EmailNotificationDispatcher leaves that notification un-emailed for the next sweep to retry.
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // 5xx = permanent per RFC 5321; 4xx is a transient failure the sender is invited to retry, which is exactly
    // what the dispatcher's retry budget is for.
    private static bool IsPermanent(SmtpStatusCode status) => (int)status >= 500;

    public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default) =>
        SendAsync(toAddress, toName, subject, body, envelopeCertificatePems: [], cancellationToken);

    public async Task SendAsync(string toAddress, string toName, string subject, string body,
        IReadOnlyList<string> envelopeCertificatePems, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Sending mail to {Recipient}.", toAddress);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(new MailboxAddress(toName, toAddress));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // S/MIME enveloping (#1334): the BODY becomes CMS EnvelopedData to the recipient's certificate —
        // the same body swap the IMAP funnel does, standard S/MIME (headers stay readable, which is why
        // the CALLER already genericized the subject). Fails OPEN to plaintext with a Warning naming the
        // fix — a stored certificate that stopped parsing must not stop the notification, and the caller
        // has already moved the details into the body, which is correct either way.
        if (envelopeCertificatePems.Count > 0)
        {
            TryEnvelopeBody(message, envelopeCertificatePems, _logger);
        }

        // Registered only when Smtp:Host is configured (see AddInfrastructure), so Host is non-null here.
        var host = _options.Host ?? throw new InvalidOperationException("SMTP host is not configured.");

        using var client = new SmtpClient();
        var secureOption = _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(host, _options.Port, secureOption, cancellationToken);

        if (!string.IsNullOrEmpty(_options.User))
        {
            await client.AuthenticateAsync(_options.User, _options.Password ?? string.Empty, cancellationToken);
        }

        try
        {
            await client.SendAsync(message, cancellationToken);
        }
        catch (SmtpCommandException e) when (IsPermanent(e.StatusCode))
        {
            // 5xx is the server saying "not now and not ever" — no such mailbox, no such domain, refused.
            // Translated at the boundary so the dispatcher can decide policy without catching a mail-library
            // type through the IEmailSender abstraction (ADR 0612).
            throw new PermanentEmailFailureException(
                $"The mail server permanently rejected {toAddress} ({(int)e.StatusCode} {e.ErrorCode}): {e.Message}", e);
        }

        await client.DisconnectAsync(quit: true, cancellationToken);
        _logger.LogDebug("Sent mail to {Recipient}.", toAddress);
    }

    /// <summary>The envelope step, its own seam so the crypto is testable without an SMTP server: the BODY
    /// becomes CMS EnvelopedData to the certificate (headers stay readable — the caller genericized the
    /// subject first). Fails OPEN to plaintext with a Warning naming the fix: a stored certificate that
    /// stopped parsing must not stop the notification, and the details already moved into the body.</summary>
    public static void TryEnvelopeBody(MimeMessage message, IReadOnlyList<string> certificatePems, ILogger logger)
    {
        if (message.Body is not { } body || certificatePems.Count == 0)
        {
            return; // a bodyless message has nothing to envelope — the enveloper family's shared refusal
        }

        var certificates = new List<System.Security.Cryptography.X509Certificates.X509Certificate2>();
        try
        {
            // A lambda, not a method group: CreateFromPem takes a ReadOnlySpan<char>, which no
            // Func<string, X509Certificate2> can bind to.
            certificates.AddRange(certificatePems.Select(pem =>
                System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem)));
            using var context = new MimeKit.Cryptography.TemporarySecureMimeContext();

            // SmimeRecipient states the content cipher; without it MimeKit falls back to 3DES (see that type).
            //
            // SEVERAL recipients over ONE ciphertext (ADR 0842): a CMS EnvelopedData wraps the same
            // content-encryption key to each recipient's public key, so addressing a reader's card AND
            // their laptop costs one key-wrap each rather than a second copy of the notification.
            var recipients = new MimeKit.Cryptography.CmsRecipientCollection();
            foreach (var certificate in certificates)
            {
                recipients.Add(SimplArchive.Infrastructure.Encryption.SmimeRecipient.For(certificate));
            }

            message.Body = MimeKit.Cryptography.ApplicationPkcs7Mime.Encrypt(context, recipients, message.Body);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            logger.LogWarning(exception,
                "The S/MIME certificate for {Recipient} could not envelope a notification — sending "
                + "plaintext. Re-upload or delete the certificate in the profile dialog.",
                message.To.ToString());
        }
        finally
        {
            // Created here, so disposed here — including on the failure path, where the old single-certificate
            // `using` used to do it and a loop cannot.
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }
}
