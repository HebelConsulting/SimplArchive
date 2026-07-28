using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Audit;

// Seals each active tenant's newly-committed audit events into immutable WORM segments in the background (ADR
// "Audit-log WORM"), off the request path. On a slow cadence (the archive lag is far shorter than any audit
// retention window measured in days) it runs AuditWormArchiver.ArchiveAsync per tenant, a no-op when there's
// nothing new. Registered unconditionally in AddInfrastructure.
public sealed class AuditWormWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditWormWorker> _logger;

    public AuditWormWorker(IServiceScopeFactory scopeFactory, ILogger<AuditWormWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(AuditWormWorker), Interval);
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
            var archiver = scope.ServiceProvider.GetRequiredService<IAuditWormArchiver>();

            var tenantIds = await dbContext.Tenants
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            var sealedTotal = 0;
            foreach (var tenantId in tenantIds)
            {
                var sealed_ = await archiver.ArchiveAsync(tenantId, cancellationToken);
                sealedTotal += sealed_;
                if (sealed_ > 0)
                {
                    _logger.LogInformation("Sealed {Count} audit event(s) into a WORM segment for tenant {TenantId}.", sealed_, tenantId);
                }
            }

            if (sealedTotal == 0)
            {
                _logger.LogDebug("Audit-WORM sweep found nothing to seal across {TenantCount} tenant(s).", tenantIds.Count);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Audit-WORM sweep failed; will retry next interval.");
        }
    }
}
