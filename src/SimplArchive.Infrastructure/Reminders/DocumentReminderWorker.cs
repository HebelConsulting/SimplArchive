using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Reminders;

// Periodically fires due document reminders (ADR "Document reminders") off the request path. Registered
// unconditionally in AddInfrastructure. The sweep is a no-op when nothing is due, so a ~1-minute tick is cheap
// and keeps due reminders reasonably prompt.
public sealed class DocumentReminderWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentReminderWorker> _logger;

    public DocumentReminderWorker(IServiceScopeFactory scopeFactory, ILogger<DocumentReminderWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(DocumentReminderWorker), Interval);
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var reminders = scope.ServiceProvider.GetRequiredService<IDocumentReminderService>();
            var acted = await reminders.SweepAsync(cancellationToken);
            if (acted > 0)
            {
                _logger.LogInformation("Document reminder sweep fired {Count} reminder(s).", acted);
            }
            else
            {
                _logger.LogDebug("Document reminder sweep found nothing due.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Document reminder sweep failed; will retry next interval.");
        }
    }
}
