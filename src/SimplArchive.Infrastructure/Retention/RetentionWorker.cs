using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Retention;

// Periodically auto-disposes documents whose retention has elapsed (ADR "Retention policies
// (auto-disposition)"), off the request path. Registered unconditionally; the sweep is a cheap no-op when no
// document is expired.
public sealed class RetentionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(IServiceScopeFactory scopeFactory, ILogger<RetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(RetentionWorker), Interval);
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
            var retention = scope.ServiceProvider.GetRequiredService<IRetentionService>();
            var disposed = await retention.SweepAsync(cancellationToken);
            if (disposed > 0)
            {
                _logger.LogInformation("Retention sweep disposed {Count} document(s).", disposed);
            }
            else
            {
                _logger.LogDebug("Retention sweep found nothing to dispose.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Retention sweep failed; will retry next interval.");
        }
    }
}
