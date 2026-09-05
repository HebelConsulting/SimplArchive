using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimplArchive.Infrastructure.Modules;

// Fires active modules' status escalations in the background (ADR 0753/flight-school #5), off the request path.
// On a slow cadence — the deadlines these watch are measured in days — it sweeps every active module and tenant
// through ModuleStatusEscalationService.SweepAsync, which no-ops when no module declares an escalation or no
// subject sits in an escalating state. Registered unconditionally in AddInfrastructure. The cadence is
// configurable via Modules:EscalationSweepIntervalMinutes (default 360).
public sealed class ModuleStatusEscalationWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ModuleStatusEscalationWorker> _logger;
    private readonly TimeSpan _interval;

    public ModuleStatusEscalationWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<ModuleStatusEscalationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<int?>("Modules:EscalationSweepIntervalMinutes") is { } m && m > 0 ? m : 360;
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(ModuleStatusEscalationWorker), _interval);
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
            var service = scope.ServiceProvider.GetRequiredService<ModuleStatusEscalationService>();
            var sent = await service.SweepAsync(DateTimeOffset.UtcNow, cancellationToken);
            if (sent > 0)
            {
                _logger.LogInformation("Module status escalation sweep sent {Count} reminder(s).", sent);
            }
            else
            {
                _logger.LogDebug("Module status escalation sweep sent nothing.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Module status escalation sweep failed; will retry next interval.");
        }
    }
}
