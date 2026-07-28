using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Worm;

// Reconciles a document's confirmed version blobs to their desired WORM lock state (ADR "WORM / immutable
// document versions"). Scoped; called at the mutation trigger sites. Best-effort: per-blob storage failures are
// logged and swallowed so the triggering action isn't broken — the compliance gap this leaves (a lock that
// failed to apply) is documented; a future reconcile worker would guarantee eventual application.
public sealed class WormLockService : IWormLockService
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorage;
    private readonly ILogger<WormLockService> _logger;

    public WormLockService(SimplArchiveDbContext dbContext, IObjectStorageClient objectStorage, ILogger<WormLockService> logger)
    {
        _dbContext = dbContext;
        _objectStorage = objectStorage;
        _logger = logger;
    }

    public async Task ReconcileAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        // Resolve regardless of soft-delete state — a recycled-but-still-retained document's blobs stay locked.
        var document = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return;
        }

        var objectKeys = await _dbContext.DocumentVersions
            .Where(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed && v.ObjectKey != null)
            .Select(v => v.ObjectKey!)
            .ToListAsync(cancellationToken);
        if (objectKeys.Count == 0)
        {
            return; // a folder or a not-yet-confirmed document — nothing to lock
        }

        var legalHoldOn = await IsDirectlyHeldAsync(documentId, cancellationToken);
        var (retainUntil, mode) = await ResolveRetentionAsync(document, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var key in objectKeys)
        {
            try
            {
                await _objectStorage.SetLegalHoldAsync(key, legalHoldOn, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "WORM: failed to set legal hold ({On}) on {Key}.", legalHoldOn, key);
            }

            if (retainUntil is { } until && until > now)
            {
                try
                {
                    // Extend-only: shortening fails at the storage layer (Compliance always, Governance without
                    // bypass) — treated as a no-op, since retention should only ever grow.
                    await _objectStorage.SetRetentionAsync(key, until, mode, cancellationToken);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "WORM: failed to set retention until {Until} on {Key} (likely a shorten attempt; kept).", until, key);
                }
            }
        }
    }

    private async Task<bool> IsDirectlyHeldAsync(Guid documentId, CancellationToken cancellationToken) =>
        await _dbContext.LegalHoldItems
            .Where(i => i.DocumentId == documentId)
            .Join(_dbContext.LegalHolds.Where(h => h.ReleasedAt == null), i => i.LegalHoldId, h => h.Id, (i, h) => h.Id)
            .AnyAsync(cancellationToken);

    private async Task<(DateTimeOffset? RetainUntil, WormLockMode Mode)> ResolveRetentionAsync(Document document, CancellationToken cancellationToken)
    {
        var mode = await _dbContext.Tenants
            .Where(t => t.Id == document.TenantId)
            .Select(t => t.WormLockMode)
            .SingleAsync(cancellationToken);

        if (document.MaskVersionId is not { } maskVersionId)
        {
            return (null, mode);
        }

        var retentionYears = await _dbContext.MaskVersions
            .Where(mv => mv.Id == maskVersionId)
            .Select(mv => mv.RetentionYears)
            .SingleOrDefaultAsync(cancellationToken);
        if (retentionYears is not { } years)
        {
            return (null, mode);
        }

        // The retention clock starts at the record's issuing date (latest confirmed version's DocumentDate),
        // falling back to when it was filed — the same anchor the retention-disposition sweep uses.
        var documentDate = await _dbContext.DocumentVersions
            .Where(v => v.DocumentId == document.Id && v.Status == DocumentVersionStatus.Confirmed)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => (DateOnly?)v.DocumentDate)
            .FirstOrDefaultAsync(cancellationToken);
        var anchor = documentDate ?? DateOnly.FromDateTime(document.CreatedAt.UtcDateTime);
        var retainUntil = new DateTimeOffset(anchor.AddYears(years).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return (retainUntil, mode);
    }
}
