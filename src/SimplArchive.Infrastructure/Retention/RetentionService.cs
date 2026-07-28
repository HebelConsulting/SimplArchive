using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Retention;

// Auto-disposes documents whose retention has elapsed (ADR "Retention policies (auto-disposition)"). Processes
// one active tenant at a time — setting the scoped tenant accessor so the DbContext's tenant + soft-delete
// query filters apply, and reindex/audit attribute to the right tenant. Eligibility + the year math are done
// client-side over the bounded candidate set (documents whose assigned mask has a retention period), since
// SQLite can't do DateOnly arithmetic in SQL. Legal-hold-frozen documents are skipped. Scoped; the hosted
// RetentionWorker calls it on a timer.
public sealed class RetentionService : IRetentionService
{
    // The stable audit action code (mirrors Api.Controllers.AuditActions.DocumentRetentionDisposed, which the
    // Infrastructure layer can't reference).
    private const string RetentionDisposedAction = "Document.RetentionDisposed";
    private const int MaxDisposalsPerTenantPerSweep = 500;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly CurrentTenantAccessor _tenantAccessor;
    private readonly ILegalHoldService _legalHold;
    private readonly IDocumentIndexQueue _indexQueue;
    private readonly IAuditRecorder _audit;

    public RetentionService(
        SimplArchiveDbContext dbContext,
        CurrentTenantAccessor tenantAccessor,
        ILegalHoldService legalHold,
        IDocumentIndexQueue indexQueue,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _legalHold = legalHold;
        _indexQueue = indexQueue;
        _audit = audit;
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        var tenants = await _dbContext.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Status == TenantStatus.Active)
            .Select(t => new { t.Id, t.RequireDispositionReview })
            .ToListAsync(cancellationToken);

        var disposed = 0;
        foreach (var tenant in tenants)
        {
            // Review-mode tenants (ADR "Retention review-before-disposition") never auto-dispose — expired
            // documents wait in the Retention tab for a records manager to Dispose or Extend.
            if (tenant.RequireDispositionReview)
            {
                continue;
            }

            // Scope every subsequent query/enqueue/audit to this tenant (the DbContext reads the same accessor).
            _tenantAccessor.TenantId = tenant.Id;
            disposed += await SweepTenantAsync(tenant.Id, today, cancellationToken);
        }

        return disposed;
    }

    private async Task<int> SweepTenantAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        // Candidates: active, leaf documents (no children — retention disposition is per-record; a
        // document-with-children, e.g. an email with attachments, is left for a later slice) whose assigned mask
        // version carries a retention period.
        var candidates = await (
            from d in _dbContext.Documents
            where d.MaskVersionId != null && !_dbContext.Documents.Any(c => c.ParentId == d.Id)
            join mv in _dbContext.MaskVersions on d.MaskVersionId equals mv.Id
            where mv.RetentionYears != null
            select new { d.Id, d.Name, d.CreatedAt, d.RetentionOverrideUntil, RetentionYears = mv.RetentionYears!.Value })
            .Take(MaxDisposalsPerTenantPerSweep)
            .ToListAsync(cancellationToken);

        var disposed = 0;
        foreach (var candidate in candidates)
        {
            // A manager's retention extension holds off disposition until its date (ADR "Retention
            // review-before-disposition").
            if (candidate.RetentionOverrideUntil is { } until && until > today)
            {
                continue;
            }

            // The retention clock starts at the record's own issuing date — the latest confirmed version's
            // DocumentDate — falling back to when it was filed.
            var documentDate = await _dbContext.DocumentVersions
                .Where(v => v.DocumentId == candidate.Id && v.Status == DocumentVersionStatus.Confirmed)
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => (DateOnly?)v.DocumentDate)
                .FirstOrDefaultAsync(cancellationToken);
            var anchor = documentDate ?? DateOnly.FromDateTime(candidate.CreatedAt.UtcDateTime);

            if (anchor.AddYears(candidate.RetentionYears) > today)
            {
                continue; // not yet expired
            }

            // A legal hold suspends disposition.
            if (await _legalHold.IsFrozenAsync(candidate.Id, cancellationToken))
            {
                continue;
            }

            var document = await _dbContext.Documents.SingleAsync(d => d.Id == candidate.Id, cancellationToken);
            document.DeletedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _indexQueue.EnqueueAsync(candidate.Id, cancellationToken); // drop it from search
            await _audit.RecordForActorAsync(
                AuditActorType.System, Guid.Empty, "Retention policy", tenantId,
                RetentionDisposedAction, "Document", candidate.Id, candidate.Name,
                $"Retention {candidate.RetentionYears}y elapsed", cancellationToken);
            disposed++;
        }

        return disposed;
    }
}
