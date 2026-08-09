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

    // How many candidates are pulled per round-trip while walking a tenant. Bounds the memory a sweep holds, not
    // how far it reaches — the walk continues until the disposal cap is hit or the tenant is exhausted.
    private const int CandidatePageSize = 500;

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
        // version carries a retention period. Ordered by Id — an arbitrary but STABLE key, and the only portable
        // one: SQLite (the test provider) refuses DateTimeOffset in ORDER BY outright, so CreatedAt is not
        // available here. Ordering happens on the entity, before the projection, or EF can't translate it.
        var candidates =
            from d in _dbContext.Documents
            where d.MaskVersionId != null && !_dbContext.Documents.Any(c => c.ParentId == d.Id)
            join mv in _dbContext.MaskVersions on d.MaskVersionId equals mv.Id
            where mv.RetentionYears != null
            orderby d.Id
            select new { d.Id, d.Name, d.CreatedAt, d.RetentionOverrideUntil, d.CurrentVersionId, RetentionYears = mv.RetentionYears!.Value };

        // The cap is on DISPOSALS, so it means what its name says. It used to sit on the candidate query as a
        // bare `.Take(500)` — before the expiry test below, which runs client-side — so it capped candidates
        // EXAMINED instead. A tenant with more than 500 retention-managed documents therefore looked at an
        // arbitrary 500 of them; if none happened to be expired it disposed nothing, the candidate set was
        // unchanged, and the next sweep asked the same question and got the same rows. Expired documents outside
        // that window were never disposed — not "caught up over successive sweeps", but never.
        //
        // So walk the whole candidate set a page at a time and stop on disposals instead. The cursor advances by
        // the page size MINUS the disposals: a disposed document is soft-deleted, which drops it from this query
        // via the soft-delete filter, and every one of them sits before the cursor in Id order — so that is
        // exactly how far the remaining rows shifted. (A document filed into a retention-carrying mask by someone
        // else mid-sweep shifts the window the other way and may be skipped; it is picked up by the next sweep,
        // which now genuinely reaches it.)
        var disposed = 0;
        var cursor = 0;
        while (disposed < MaxDisposalsPerTenantPerSweep)
        {
            var page = await candidates.Skip(cursor).Take(CandidatePageSize).ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                break; // the tenant is exhausted
            }

            var disposedBefore = disposed;
            disposed += await DisposePageAsync(page.Select(c => new Candidate(
                c.Id, c.Name, c.CreatedAt, c.RetentionOverrideUntil, c.CurrentVersionId, c.RetentionYears)).ToList(),
                tenantId, today, MaxDisposalsPerTenantPerSweep - disposed, cancellationToken);

            cursor += page.Count - (disposed - disposedBefore);
        }

        return disposed;
    }

    private sealed record Candidate(
        Guid Id, string Name, DateTimeOffset CreatedAt, DateOnly? RetentionOverrideUntil, Guid? CurrentVersionId, int RetentionYears);

    // Disposes the expired documents in one page, up to `remaining`. Returns how many it disposed.
    private async Task<int> DisposePageAsync(
        IReadOnlyList<Candidate> page, Guid tenantId, DateOnly today, int remaining, CancellationToken cancellationToken)
    {
        var disposed = 0;
        foreach (var candidate in page)
        {
            if (disposed >= remaining)
            {
                break;
            }

            // A manager's retention extension holds off disposition until its date (ADR "Retention
            // review-before-disposition").
            if (candidate.RetentionOverrideUntil is { } until && until > today)
            {
                continue;
            }

            // The retention clock starts at the record's own issuing date — the current version's DocumentDate
            // honoring the CurrentVersionId pointer (issue #265), else the latest confirmed — falling back to when
            // it was filed.
            var currentVersion = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, candidate.Id, candidate.CurrentVersionId, cancellationToken);
            var anchor = currentVersion?.DocumentDate ?? DateOnly.FromDateTime(candidate.CreatedAt.UtcDateTime);

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
