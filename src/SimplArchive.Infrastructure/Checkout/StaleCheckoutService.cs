using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Checkout;

// Auto-releases stale check-outs (ADR "Stale check-out auto-release sweep"). Processes one active tenant with
// CheckoutTtlDays > 0 at a time — setting the scoped tenant accessor so the DbContext's tenant filter applies
// and the audit/notification attribute to the right tenant. The idle comparison is done client-side over the
// (small) set of currently-checked-out documents, since SQLite can't translate a DateTimeOffset comparison in
// SQL (same reason as the workflow-escalation sweep). Scoped; the hosted StaleCheckoutWorker calls it on a timer.
public sealed class StaleCheckoutService : IStaleCheckoutService
{
    // The stable audit action code (mirrors Api.Controllers.AuditActions.DocumentCheckoutExpired, which the
    // Infrastructure layer can't reference).
    private const string CheckoutExpiredAction = "Document.CheckoutExpired";

    private readonly SimplArchiveDbContext _dbContext;
    private readonly CurrentTenantAccessor _tenantAccessor;
    private readonly IObjectStorageClient _objectStorage;
    private readonly IAuditRecorder _audit;
    private readonly INotificationService _notifications;
    private readonly ILogger<StaleCheckoutService> _logger;

    public StaleCheckoutService(
        SimplArchiveDbContext dbContext,
        CurrentTenantAccessor tenantAccessor,
        IObjectStorageClient objectStorage,
        IAuditRecorder audit,
        INotificationService notifications,
        ILogger<StaleCheckoutService> logger)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _objectStorage = objectStorage;
        _audit = audit;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var tenants = await _dbContext.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Status == TenantStatus.Active && t.CheckoutTtlDays > 0)
            .Select(t => new { t.Id, t.CheckoutTtlDays, t.CheckoutWarningDays })
            .ToListAsync(cancellationToken);

        var released = 0;
        foreach (var tenant in tenants)
        {
            // Scope every subsequent query/stash/audit/notify to this tenant (the DbContext reads the accessor).
            _tenantAccessor.TenantId = tenant.Id;
            released += await SweepTenantAsync(tenant.Id, tenant.CheckoutTtlDays, tenant.CheckoutWarningDays, now, cancellationToken);
        }

        return released;
    }

    private async Task<int> SweepTenantAsync(Guid tenantId, int ttlDays, int warningDays, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var releaseCutoff = now.AddDays(-ttlDays);
        // Warn once the check-out is within `warningDays` of its release (i.e. idle at least ttl-warning days).
        var warningCutoff = now.AddDays(-(ttlDays - warningDays));

        var candidates = await _dbContext.Documents
            .Where(d => d.CheckedOutByUserId != null)
            .Select(d => new { d.Id, d.Name, HolderId = d.CheckedOutByUserId!.Value, At = d.CheckedOutAt!.Value, d.CheckoutReminderSentAt })
            .ToListAsync(cancellationToken);

        var released = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.At > releaseCutoff)
            {
                // Not yet due for release — but if it's in the grace window and hasn't been warned, warn once.
                if (warningDays > 0 && candidate.CheckoutReminderSentAt is null && candidate.At <= warningCutoff)
                {
                    var doc = await _dbContext.Documents.SingleAsync(d => d.Id == candidate.Id, cancellationToken);
                    doc.CheckoutReminderSentAt = now;
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var expiresAt = candidate.At.AddDays(ttlDays);
                    await _notifications.NotifyAsync(
                        candidate.HolderId, NotificationType.CheckoutExpiring,
                        "Check-out expiring soon",
                        $"Your check-out of \"{candidate.Name}\" will be automatically released on {expiresAt:yyyy-MM-dd} unless you check it in — any working copy not checked in will be discarded.",
                        candidate.Id, cancellationToken);
                }

                continue;
            }

            var document = await _dbContext.Documents.SingleAsync(d => d.Id == candidate.Id, cancellationToken);
            document.CheckedOutByUserId = null;
            document.CheckedOutAt = null;
            document.CheckoutReminderSentAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Delete the former holder's cloud working-copy stash (best-effort — the lock is already released).
            try
            {
                await _objectStorage.DeleteObjectAsync(
                    CheckoutStashKey.Build(tenantId, candidate.HolderId, candidate.Id), cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to delete the stash for auto-released check-out {DocumentId}.", candidate.Id);
            }

            await _audit.RecordForActorAsync(
                AuditActorType.System, Guid.Empty, "System", tenantId,
                CheckoutExpiredAction, "Document", candidate.Id, candidate.Name,
                $"Check-out idle past the tenant threshold auto-released", cancellationToken);

            await _notifications.NotifyAsync(
                candidate.HolderId, NotificationType.CheckoutExpired,
                "Check-out released",
                $"Your check-out of \"{candidate.Name}\" was automatically released after a period of inactivity. Any working copy not checked in has been discarded.",
                candidate.Id, cancellationToken);

            released++;
        }

        return released;
    }
}
