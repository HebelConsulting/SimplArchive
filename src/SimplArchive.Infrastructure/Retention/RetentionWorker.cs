using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Retention;

// Periodically auto-disposes documents whose retention has elapsed (ADR "Retention policies
// (auto-disposition)"), off the request path. Registered unconditionally; the sweep is a cheap no-op when no
// document is expired. The schedule comes from RetentionSweepOptions — see there for why a host must be able
// to control it.
public sealed class RetentionWorker : BackgroundService
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(IServiceScopeFactory scopeFactory, IOptions<RetentionSweepOptions> options, ILogger<RetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = options.Value.Interval;
        _initialDelay = options.Value.InitialDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(RetentionWorker), _interval);
        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(_interval, stoppingToken);
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
