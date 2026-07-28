using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Storage;

// Per-tenant storage quota accounting/enforcement (ADR "Per-tenant storage quota"). Tenant isn't ITenantScoped
// (it's the root, no query filter), so it's looked up by Id directly. Scoped. Soft-quota warnings (ADR "Storage
// soft-quota warnings") are evaluated after each usage change.
public sealed class StorageQuotaService : IStorageQuotaService
{
    // Soft-quota warning thresholds (percent of the quota). Crossing one up notifies the tenant's admins; a
    // level fires once and re-arms only after usage drops back below it (tracked by Tenant.StorageWarningLevel).
    // Integer percent math (used*100 vs quota*threshold) — an exact boundary, unlike a floating-point fraction
    // (1000 * 0.80 = 800.00000000000004, which 800 isn't >= ).
    private const int WarnPercent = 80;
    private const int CriticalPercent = 95;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ILogger<StorageQuotaService> _logger;

    public StorageQuotaService(SimplArchiveDbContext dbContext, ILogger<StorageQuotaService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<bool> CanStoreAsync(Guid tenantId, long additionalBytes, CancellationToken cancellationToken = default)
    {
        var quota = await _dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.StorageQuotaBytes, t.StorageUsedBytes })
            .SingleOrDefaultAsync(cancellationToken);

        // No tenant row (shouldn't happen) or no quota set → unlimited.
        return quota?.StorageQuotaBytes is not { } limit || quota.StorageUsedBytes + additionalBytes <= limit;
    }

    public async Task AdjustUsageAsync(Guid tenantId, long deltaBytes, CancellationToken cancellationToken = default)
    {
        if (deltaBytes == 0)
        {
            return;
        }

        // Atomic DB-level increment so concurrent adjustments don't race a read-modify-write. Clamp at 0 defensively
        // (a decrement can't drive the counter negative even if a blob was never counted at add time).
        await _dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(
                    t => t.StorageUsedBytes,
                    t => t.StorageUsedBytes + deltaBytes < 0 ? 0 : t.StorageUsedBytes + deltaBytes),
                cancellationToken);

        await EvaluateWarningAsync(tenantId, cancellationToken);
    }

    // Compares the new usage against the soft-quota thresholds and, if the level crossed upward, notifies the
    // tenant's admins; a downward cross just re-arms the level. Best-effort — a warning failure never breaks the
    // usage accounting.
    private async Task EvaluateWarningAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            // Load the tenant tracked (Tenant isn't ITenantScoped, so no filter to bypass) so the level change +
            // notification inserts commit together in one SaveChanges — mixing a second ExecuteUpdate with the
            // notification SaveChanges on the same context left the level uncommitted.
            var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
            if (tenant?.StorageQuotaBytes is not { } quota || quota <= 0)
            {
                return; // unlimited → no thresholds
            }

            var used = tenant.StorageUsedBytes;
            var level = used * 100 >= quota * CriticalPercent ? 2 : used * 100 >= quota * WarnPercent ? 1 : 0;
            var previous = tenant.StorageWarningLevel;
            if (level == previous)
            {
                return; // no change
            }

            tenant.StorageWarningLevel = level;

            // Only notify on an upward cross; a drop below a threshold just re-arms the level.
            if (level > previous)
            {
                // Notify each active tenant admin directly (not via INotificationService, whose self-skip would
                // drop an admin who did the uploading — every admin should be warned). Emailed automatically by
                // the dispatcher (StorageQuotaWarning isn't a user-mutable type). IgnoreQueryFilters: the
                // accounting caller's ambient tenant may differ (background workers process any tenant's doc).
                var admins = await _dbContext.Users
                    .IgnoreQueryFilters()
                    .Where(u => u.TenantId == tenantId && u.IsTenantAdmin && u.IsActive)
                    .Select(u => u.Id)
                    .ToListAsync(cancellationToken);

                var percent = (int)(used * 100 / quota);
                var title = level == 2 ? "Storage almost full" : "Storage approaching quota";
                var body = $"Your organization's storage is at {percent}% of its {Mb(quota)} quota ({Mb(used)} used). " +
                    "Free up space or increase the quota before uploads are refused.";
                var now = DateTimeOffset.UtcNow;

                foreach (var adminId in admins)
                {
                    _dbContext.Notifications.Add(new Notification
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        RecipientUserId = adminId,
                        Type = NotificationType.StorageQuotaWarning,
                        Title = title,
                        Body = body,
                        CreatedAt = now,
                    });
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to evaluate the storage soft-quota warning for tenant {Tenant}.", tenantId);
        }
    }

    private static string Mb(long bytes) => $"{bytes / (1024 * 1024)} MB";
}
