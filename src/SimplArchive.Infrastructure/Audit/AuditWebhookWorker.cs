using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Audit;

// Streams each active tenant's newly-committed audit events to its configured SIEM webhook in the background
// (ADR "Audit webhook streaming"), off the request path. On a short cadence it runs AuditWebhookDispatcher per
// tenant that has a webhook URL — a no-op for the rest. Registered unconditionally in AddInfrastructure (it's
// idle until a tenant configures a webhook).
public sealed class AuditWebhookWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditWebhookWorker> _logger;

    public AuditWebhookWorker(IServiceScopeFactory scopeFactory, ILogger<AuditWebhookWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(AuditWebhookWorker), Interval);
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
            var dispatcher = scope.ServiceProvider.GetRequiredService<IAuditWebhookDispatcher>();

            var tenantIds = await dbContext.Tenants
                .Where(t => t.Status == TenantStatus.Active && t.AuditWebhookUrl != null)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            var deliveredTotal = 0;
            foreach (var tenantId in tenantIds)
            {
                var delivered = await dispatcher.DispatchAsync(tenantId, cancellationToken);
                deliveredTotal += delivered;
                if (delivered > 0)
                {
                    _logger.LogInformation("Streamed {Count} audit event(s) to the webhook for tenant {TenantId}.", delivered, tenantId);
                }
            }

            if (deliveredTotal == 0)
            {
                _logger.LogDebug("Audit-webhook sweep delivered nothing across {TenantCount} configured tenant(s).", tenantIds.Count);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Audit-webhook sweep failed; will retry next interval.");
        }
    }
}
