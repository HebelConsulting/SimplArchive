using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Api.Errors.Exceptions.Retention;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The records-retention schedule (ADR "Retention policies (auto-disposition)") — the documents that carry a
/// retention period (via their assigned mask), with the computed disposition date + status, so an admin can see
/// what the retention sweep will auto-dispose. Read-only; the disposition itself happens automatically in the
/// background (RetentionWorker). Gated on <c>CanManageClassification</c> — the dedicated records-management
/// right (a User-only right; a ServiceAccount has none). The computed disposition date can't be expressed in a
/// keyset cursor, so this bounded admin view returns up to a cap (soonest-first) with a <c>truncated</c> flag
/// rather than paginating — the same "small bounded catalog" treatment as the masks/reviewers lists.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/retention")]
[Authorize]
public class RetentionController : ControllerBase
{
    private const int MaxItems = 500;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly ILegalHoldService _legalHold;
    private readonly IDocumentIndexQueue _indexQueue;
    private readonly IAuditRecorder _audit;

    public RetentionController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IUserSystemRightsResolver userSystemRights,
        ILegalHoldService legalHold,
        IDocumentIndexQueue indexQueue,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _userSystemRights = userSystemRights;
        _legalHold = legalHold;
        _indexQueue = indexQueue;
        _audit = audit;
    }

    public class RetentionScheduleResource : HypermediaResource
    {
        public List<RetentionItemResource> Items { get; set; } = [];

        // True when more documents carry a retention period than the returned cap.
        public bool Truncated { get; set; }

        // Whether this tenant requires disposition review (ADR "Retention review-before-disposition") — the
        // client shows the Dispose/Extend actions as a review queue and notes the auto-sweep is off.
        public bool RequiresReview { get; set; }
    }

    public class RetentionItemResource
    {
        public Guid DocumentId { get; set; }
        public string DocumentName { get; set; } = "";
        public int RetentionYears { get; set; }

        // The date the document becomes eligible for auto-disposition (anchor + RetentionYears), "yyyy-MM-dd".
        public string DispositionDate { get; set; } = "";

        // Past its disposition date but not yet swept (the sweep runs periodically).
        public bool Overdue { get; set; }

        // Under an active legal hold — disposition is suspended until the hold is released.
        public bool SuspendedByHold { get; set; }

        // A records manager's retention extension (ADR "Retention review-before-disposition"), "yyyy-MM-dd" or
        // null. While in the future, the document is retained past its mask-computed disposition date.
        public string? RetentionOverrideUntil { get; set; }
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule(CancellationToken cancellationToken)
    {
        if (!await CanManageClassificationAsync(cancellationToken))
        {
            return Forbid();
        }

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        // Leaf documents (no children) whose assigned mask version carries a retention period. Cap the scan.
        var candidates = await (
            from d in _dbContext.Documents
            where d.MaskVersionId != null && !_dbContext.Documents.Any(c => c.ParentId == d.Id)
            join mv in _dbContext.MaskVersions on d.MaskVersionId equals mv.Id
            where mv.RetentionYears != null
            select new { d.Id, d.Name, d.CreatedAt, d.RetentionOverrideUntil, RetentionYears = mv.RetentionYears!.Value })
            .Take(MaxItems + 1)
            .ToListAsync(cancellationToken);

        var truncated = candidates.Count > MaxItems;
        var items = new List<RetentionItemResource>();
        foreach (var candidate in candidates.Take(MaxItems))
        {
            var documentDate = await _dbContext.DocumentVersions
                .Where(v => v.DocumentId == candidate.Id && v.Status == DocumentVersionStatus.Confirmed)
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => (DateOnly?)v.DocumentDate)
                .FirstOrDefaultAsync(cancellationToken);
            var anchor = documentDate ?? DateOnly.FromDateTime(candidate.CreatedAt.UtcDateTime);
            var dispositionDate = anchor.AddYears(candidate.RetentionYears);

            // A manager's extension pushes the effective disposition date out — that's what "overdue" compares.
            var effectiveDate = candidate.RetentionOverrideUntil is { } o && o > dispositionDate ? o : dispositionDate;

            items.Add(new RetentionItemResource
            {
                DocumentId = candidate.Id,
                DocumentName = candidate.Name,
                RetentionYears = candidate.RetentionYears,
                DispositionDate = dispositionDate.ToString("yyyy-MM-dd"),
                Overdue = effectiveDate <= today,
                SuspendedByHold = await _legalHold.IsFrozenAsync(candidate.Id, cancellationToken),
                RetentionOverrideUntil = candidate.RetentionOverrideUntil?.ToString("yyyy-MM-dd"),
            });
        }

        // Soonest disposition first.
        items.Sort((a, b) => string.CompareOrdinal(a.DispositionDate, b.DispositionDate));

        var requiresReview = _currentTenantAccessor.TenantId is { } tenantId
            && await _dbContext.Tenants.Where(t => t.Id == tenantId).Select(t => t.RequireDispositionReview).FirstOrDefaultAsync(cancellationToken);

        return Ok(new RetentionScheduleResource
        {
            Items = items,
            Truncated = truncated,
            RequiresReview = requiresReview,
            Links = [new Link("self", "/api/retention/schedule", "GET")],
        });
    }

    [HttpHead("schedule")]
    public async Task<IActionResult> HeadSchedule(CancellationToken cancellationToken) =>
        await CanManageClassificationAsync(cancellationToken) ? NoContent() : Forbid();

    // ---- Disposition review (ADR "Retention review-before-disposition") ---------------------------------

    public class ExtendRetentionRequest
    {
        // The new "retain until" date, "yyyy-MM-dd". Must be in the future.
        public string? Until { get; set; }
    }

    // Manually disposes an eligible document — the review-mode equivalent of the auto-sweep (a POST action
    // sub-resource, a genuine state change like restore/purge). Soft-deletes it to the recycle bin; the manager
    // is recorded as the actor (not System). Refused for a document that isn't past its (possibly extended)
    // disposition date, or one under an active legal hold.
    [HttpPost("{documentId:guid}/dispose")]
    public async Task<IActionResult> Dispose(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await CanManageClassificationAsync(cancellationToken))
        {
            return Forbid();
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!await IsEligibleForDispositionAsync(document, cancellationToken))
        {
            throw new DocumentNotEligibleForDispositionException();
        }

        if (await _legalHold.IsFrozenAsync(documentId, cancellationToken))
        {
            throw new DocumentUnderLegalHoldException(); // compliance overrides disposition
        }

        document.DeletedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _indexQueue.EnqueueAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentRetentionDisposed, "Document", documentId, document.Name, "Disposed on review", cancellationToken: cancellationToken);
        return NoContent();
    }

    // Extends a document's retention to a future date — the "retain, don't destroy" decision. Sets the
    // per-document override so neither the auto-sweep nor a manual dispose will act until then.
    [HttpPost("{documentId:guid}/extend")]
    public async Task<IActionResult> Extend(Guid documentId, [FromBody] ExtendRetentionRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageClassificationAsync(cancellationToken))
        {
            return Forbid();
        }

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!DateOnly.TryParseExact(request.Until, "yyyy-MM-dd", out var until) || until <= today)
        {
            throw new InvalidRetentionExtensionException();
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        document.RetentionOverrideUntil = until;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(AuditActions.DocumentRetentionExtended, "Document", documentId, document.Name, $"Retained until {until:yyyy-MM-dd}", cancellationToken: cancellationToken);
        return NoContent();
    }

    // Eligible = a leaf document whose assigned mask carries a retention period, past its (override-adjusted)
    // disposition date. Mirrors RetentionService's sweep eligibility (minus the legal-hold check, done at the
    // call site so it can raise the specific LEGAL_HOLD error).
    private async Task<bool> IsEligibleForDispositionAsync(Document document, CancellationToken cancellationToken)
    {
        if (document.MaskVersionId is not { } maskVersionId)
        {
            return false;
        }

        if (await _dbContext.Documents.AnyAsync(c => c.ParentId == document.Id, cancellationToken))
        {
            return false; // not a leaf
        }

        var retentionYears = await _dbContext.MaskVersions.Where(mv => mv.Id == maskVersionId).Select(mv => mv.RetentionYears).FirstOrDefaultAsync(cancellationToken);
        if (retentionYears is not { } years)
        {
            return false;
        }

        var documentDate = await _dbContext.DocumentVersions
            .Where(v => v.DocumentId == document.Id && v.Status == DocumentVersionStatus.Confirmed)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => (DateOnly?)v.DocumentDate)
            .FirstOrDefaultAsync(cancellationToken);
        var anchor = documentDate ?? DateOnly.FromDateTime(document.CreatedAt.UtcDateTime);
        var dispositionDate = anchor.AddYears(years);
        var effectiveDate = document.RetentionOverrideUntil is { } o && o > dispositionDate ? o : dispositionDate;

        return effectiveDate <= DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
    }

    private async Task<bool> CanManageClassificationAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageClassification;
        }

        return false;
    }
}
