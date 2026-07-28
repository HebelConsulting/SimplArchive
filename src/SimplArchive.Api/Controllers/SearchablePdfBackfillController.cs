using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Backfills searchable-PDF successors for existing scanned documents that predate the feature (ADRs "Backfill
/// searchable PDFs for existing TIFFs" and "Scanned image-only PDF detection"): finds every document whose
/// latest confirmed version is a TIFF (always converted) or a PDF (converted only if the worker detects a
/// scanned image-only document) and enqueues a conversion, which the SearchablePdfWorker then processes with
/// each version's OCR-language override / tenant default. Allowed for a PlatformAdministrator (all tenants)
/// or a tenant administrator (their own tenant). GET reports how many are pending; POST enqueues them.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/searchable-pdf/backfill")]
[Authorize]
public class SearchablePdfBackfillController : ControllerBase
{
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ISearchablePdfQueue _queue;
    private readonly ICurrentPlatformAdministratorAccessor _platformAdministratorAccessor;
    private readonly ICurrentUserAccessor _userAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;

    public SearchablePdfBackfillController(
        SimplArchiveDbContext dbContext,
        ISearchablePdfQueue queue,
        ICurrentPlatformAdministratorAccessor platformAdministratorAccessor,
        ICurrentUserAccessor userAccessor,
        IUserSystemRightsResolver userSystemRights)
    {
        _dbContext = dbContext;
        _queue = queue;
        _platformAdministratorAccessor = platformAdministratorAccessor;
        _userAccessor = userAccessor;
        _userSystemRights = userSystemRights;
    }

    public class BackfillResource
    {
        // Candidate current TIFF/PDF versions (GET) / how many were enqueued (POST).
        public int Count { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Trigger(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is not { } allTenants)
        {
            return Forbid();
        }

        var candidates = await FindCurrentScanCandidatesAsync(allTenants, cancellationToken);
        var enqueued = await _queue.EnqueueManyAsync(candidates, cancellationToken);

        return Accepted(new BackfillResource { Count = enqueued });
    }

    [HttpGet]
    public async Task<IActionResult> Pending(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is not { } allTenants)
        {
            return Forbid();
        }

        var candidates = await FindCurrentScanCandidatesAsync(allTenants, cancellationToken);
        return Ok(new BackfillResource { Count = candidates.Count });
    }

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        await ResolveScopeAsync(cancellationToken) is null ? Forbid() : NoContent();

    // null = not authorized; true = platform admin (all tenants); false = tenant admin (own tenant, via the
    // tenant query filter already set for a User token).
    private async Task<bool?> ResolveScopeAsync(CancellationToken cancellationToken)
    {
        if (_platformAdministratorAccessor.PlatformAdministratorId is not null)
        {
            return true;
        }

        if (_userAccessor.UserId is { } userId)
        {
            // Effective IsTenantAdmin (own ∪ groups) — ADR "Enforce group system rights for members".
            var isTenantAdmin = (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;
            if (isTenantAdmin)
            {
                return false;
            }
        }

        return null;
    }

    // Every confirmed TIFF **or PDF** version that is its document's latest confirmed version and isn't already
    // queued, for a live (not soft-deleted) document. A latest-confirmed TIFF has no searchable successor yet;
    // a latest-confirmed PDF is a *candidate* — the worker runs detection and only OCRs a scanned image-only
    // one (ADR "Scanned image-only PDF detection"), so this count is an upper bound for PDFs (born-digital /
    // already-OCR'd successors are enqueued but dropped by the worker). Scoped to all tenants (platform admin)
    // or the caller's tenant (the query filters apply for a tenant-admin User).
    private async Task<List<SearchablePdfJob>> FindCurrentScanCandidatesAsync(bool allTenants, CancellationToken cancellationToken)
    {
        var versions = allTenants ? _dbContext.DocumentVersions.IgnoreQueryFilters(TenantFilterOnly) : _dbContext.DocumentVersions;
        var documents = allTenants ? _dbContext.Documents.IgnoreQueryFilters(TenantFilterOnly) : _dbContext.Documents;

        var rows = await versions
            .Where(v => v.Status == DocumentVersionStatus.Confirmed
                && (v.ObjectKey.ToLower().EndsWith(".tif") || v.ObjectKey.ToLower().EndsWith(".tiff") || v.ObjectKey.ToLower().EndsWith(".pdf")))
            // the latest confirmed version of its document (no newer version => no successor yet)
            .Where(v => !versions.Any(v2 => v2.DocumentId == v.DocumentId && v2.Status == DocumentVersionStatus.Confirmed && v2.VersionNumber > v.VersionNumber))
            // the document is live (the Documents filter drops soft-deleted rows)
            .Where(v => documents.Any(d => d.Id == v.DocumentId))
            // not already queued
            .Where(v => !_dbContext.SearchablePdfOutbox.Any(o => o.SourceVersionId == v.Id))
            .Select(v => new { v.Id, v.DocumentId, v.TenantId })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new SearchablePdfJob(r.TenantId, r.DocumentId, r.Id)).ToList();
    }
}
