namespace SimplArchive.Application.Abstractions;

// Sends a plain-text email (ADR "Email notifications (SMTP)"). Implemented by SmtpEmailSender (MailKit) when an
// SMTP host is configured, else NullEmailSender (logs and drops) so tests / SMTP-less deployments still run.
public interface IEmailSender
{
    Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default);

    // The enveloping overload (#1334): when a certificate is supplied, the sender S/MIME-encrypts the BODY
    // to it before submission (the subject stays whatever the caller chose — it cannot be encrypted, which
    // is why the caller genericizes it first). Sugar for the LIST below, which is the one an implementation
    // overrides; kept because the self-service column holds exactly one certificate.
    Task SendAsync(string toAddress, string toName, string subject, string body,
        string? envelopeCertificatePem, CancellationToken cancellationToken = default) =>
        SendAsync(toAddress, toName, subject, body,
            envelopeCertificatePem is { Length: > 0 } pem ? new[] { pem } : [], cancellationToken);

    // SEVERAL certificates (ADR 0856): a module answers a SET — the same person's card and their laptop —
    // and one CMS EnvelopedData addresses all of them over ONE copy of the ciphertext, so the reader opens
    // it with whichever key is to hand.
    //
    // THIS is the overload an implementation overrides, and the single-certificate one above is sugar for
    // it. The other way round was written first and is a trap: a list-to-single adapter has to drop all but
    // one certificate, so a reader who happened to hold the second key would silently stop being able to
    // read their own notifications.
    //
    // Default: drops the certificates and sends plaintext, so test fakes needn't implement it — a fake
    // reaching this default is a fake no enveloping test should be running against.
    Task SendAsync(string toAddress, string toName, string subject, string body,
        IReadOnlyList<string> envelopeCertificatePems, CancellationToken cancellationToken = default) =>
        SendAsync(toAddress, toName, subject, body, cancellationToken);
}
