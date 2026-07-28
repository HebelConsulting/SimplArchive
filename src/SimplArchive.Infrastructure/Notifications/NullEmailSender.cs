using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Notifications;

// The IEmailSender used when no SMTP host is configured (ADR "Email notifications (SMTP)") — logs the message
// and drops it. Registered instead of SmtpEmailSender for tests / SMTP-less deployments; since the
// EmailNotificationWorker is only registered when SMTP is configured, this sender only ever runs if something
// calls IEmailSender directly.
public sealed class NullEmailSender : IEmailSender
{
    private readonly ILogger<NullEmailSender> _logger;

    public NullEmailSender(ILogger<NullEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Email sending disabled (no SMTP host); dropping message to {Address}: {Subject}", toAddress, subject);
        return Task.CompletedTask;
    }
}
