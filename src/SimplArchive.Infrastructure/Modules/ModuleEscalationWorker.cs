using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimplArchive.Infrastructure.Modules;

// Runs the module-license escalation sweep (ADR 0740) in the background — the StaleCheckoutWorker shape.
// The thresholds are measured in days, so the cadence is slow: Modules:EscalationSweepIntervalMinutes
// (default 360). Level-crossing dedup lives in the service, so a faster cadence never spams — it only
// narrows how late after midnight a cross is noticed.
public sealed class ModuleEscalationWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ModuleEscalationWorker> _logger;
    private readonly TimeSpan _interval;

    public ModuleEscalationWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<ModuleEscalationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<int?>("Modules:EscalationSweepIntervalMinutes") is { } m && m > 0 ? m : 360;
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(ModuleEscalationWorker), _interval);
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
            var service = scope.ServiceProvider.GetRequiredService<ModuleEscalationService>();
            var notified = await service.SweepAsync(DateTimeOffset.UtcNow, cancellationToken);
            if (notified > 0)
            {
                _logger.LogInformation("Module-license escalation wrote {Count} notification(s).", notified);
            }
            else
            {
                _logger.LogDebug("Module-license escalation sweep found no level crossings.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Module-license escalation sweep failed; will retry next interval.");
        }
    }
}
