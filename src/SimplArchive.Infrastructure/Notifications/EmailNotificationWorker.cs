using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Notifications;

// Periodically emails the not-yet-emailed in-app notifications (ADR "Email notifications (SMTP)"), off the
// request path. Registered only when SMTP is configured; a short interval keeps notification email prompt while
// staying a cheap no-op when nothing is pending.
public sealed class EmailNotificationWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailNotificationWorker> _logger;

    public EmailNotificationWorker(IServiceScopeFactory scopeFactory, ILogger<EmailNotificationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(EmailNotificationWorker), Interval);
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await DispatchAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IEmailNotificationDispatcher>();
            var sent = await dispatcher.DispatchPendingAsync(cancellationToken);
            if (sent > 0)
            {
                _logger.LogInformation("Emailed {Count} notification(s).", sent);
            }
            else
            {
                _logger.LogDebug("Notification email sweep found nothing pending.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Notification email sweep failed; will retry next interval.");
        }
    }
}
