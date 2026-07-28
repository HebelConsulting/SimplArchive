using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Audit;

// Enforces each tenant's audit-log retention window in the background (ADR "Audit trail retention and purge"),
// off the request path. On a slow cadence (retention is measured in days) it sweeps every active tenant through
// AuditRetentionService.PurgeAsync, which no-ops for tenants that keep events forever (AuditRetentionDays = 0)
// or have nothing old enough. Registered unconditionally in AddInfrastructure. Manual purge runs the same
// service via POST /api/audit-events/purge.
public sealed class AuditRetentionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditRetentionWorker> _logger;

    public AuditRetentionWorker(IServiceScopeFactory scopeFactory, ILogger<AuditRetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(AuditRetentionWorker), Interval);
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
            var dbContext = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var retention = scope.ServiceProvider.GetRequiredService<IAuditRetentionService>();

            var tenantIds = await dbContext.Tenants
                .Where(t => t.Status == TenantStatus.Active && t.AuditRetentionDays > 0)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            var purgedTotal = 0;
            foreach (var tenantId in tenantIds)
            {
                var purged = await retention.PurgeAsync(tenantId, cancellationToken);
                purgedTotal += purged;
                if (purged > 0)
                {
                    _logger.LogInformation("Purged {Count} aged audit events for tenant {TenantId}.", purged, tenantId);
                }
            }

            if (purgedTotal == 0)
            {
                _logger.LogDebug("Audit-retention sweep found nothing to purge across {TenantCount} tenant(s).", tenantIds.Count);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Audit-retention sweep failed; will retry next interval.");
        }
    }
}
