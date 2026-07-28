namespace SimplArchive.Application.Abstractions;

// Sends a plain-text email (ADR "Email notifications (SMTP)"). Implemented by SmtpEmailSender (MailKit) when an
// SMTP host is configured, else NullEmailSender (logs and drops) so tests / SMTP-less deployments still run.
public interface IEmailSender
{
    Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default);
}
