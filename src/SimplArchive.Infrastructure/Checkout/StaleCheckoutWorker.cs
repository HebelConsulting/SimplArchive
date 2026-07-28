using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Checkout;

// Auto-releases stale check-outs in the background (ADR "Stale check-out auto-release sweep"), off the request
// path. On a slow cadence (the threshold is measured in days) it sweeps every active tenant through
// StaleCheckoutService.SweepAsync, which no-ops for tenants that never expire (CheckoutTtlDays = 0) or have
// nothing idle enough. Registered unconditionally in AddInfrastructure. The cadence is configurable via
// Checkout:SweepIntervalMinutes (default 60, ADR "Check-out expiry UX").
public sealed class StaleCheckoutWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StaleCheckoutWorker> _logger;
    private readonly TimeSpan _interval;

    public StaleCheckoutWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<StaleCheckoutWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<int?>("Checkout:SweepIntervalMinutes") is { } m && m > 0 ? m : 60;
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(StaleCheckoutWorker), _interval);
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
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
            var service = scope.ServiceProvider.GetRequiredService<IStaleCheckoutService>();
            var released = await service.SweepAsync(cancellationToken);
            if (released > 0)
            {
                _logger.LogInformation("Auto-released {Count} stale check-out(s).", released);
            }
            else
            {
                _logger.LogDebug("Stale-check-out sweep found nothing to release.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Stale-check-out sweep failed; will retry next interval.");
        }
    }
}
