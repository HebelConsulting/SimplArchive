using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Notifications;

// Logs a message and drops it. NO LONGER REGISTERED BY DEFAULT (#1337): an SMTP account can now arrive at
// runtime from a tenant, so the container cannot decide at startup that mail is impossible — and dropping was
// the wrong answer anyway, since a notification somebody was asked to act on vanished with an Information line
// as its only trace. SmtpEmailSender now refuses instead, and the message stays queued for the retry path.
//
// Kept for a test or a deployment that wants mail explicitly discarded, which must now be wired on purpose.
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
