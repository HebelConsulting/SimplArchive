namespace SimplArchive.Application.Abstractions;

// Sends a plain-text email (ADR "Email notifications (SMTP)"). Implemented by SmtpEmailSender (MailKit) when an
// SMTP host is configured, else NullEmailSender (logs and drops) so tests / SMTP-less deployments still run.
public interface IEmailSender
{
    Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default);

    // The enveloping overload (#1334): when envelopeCertificatePem is supplied, the sender S/MIME-encrypts
    // the BODY to that certificate before submission (the subject stays whatever the caller chose — it
    // cannot be encrypted, which is why the caller genericizes it first). Default: drops the certificate
    // and sends plaintext, so test fakes needn't implement it — a fake reaching this default is a fake no
    // enveloping test should be running against.
    Task SendAsync(string toAddress, string toName, string subject, string body,
        string? envelopeCertificatePem, CancellationToken cancellationToken = default) =>
        SendAsync(toAddress, toName, subject, body, cancellationToken);
}
