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

    public async Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Sending mail to {Recipient}.", toAddress);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(new MailboxAddress(toName, toAddress));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // Registered only when Smtp:Host is configured (see AddInfrastructure), so Host is non-null here.
        var host = _options.Host ?? throw new InvalidOperationException("SMTP host is not configured.");

        using var client = new SmtpClient();
        var secureOption = _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(host, _options.Port, secureOption, cancellationToken);

        if (!string.IsNullOrEmpty(_options.User))
        {
            await client.AuthenticateAsync(_options.User, _options.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
        _logger.LogDebug("Sent mail to {Recipient}.", toAddress);
    }
}
