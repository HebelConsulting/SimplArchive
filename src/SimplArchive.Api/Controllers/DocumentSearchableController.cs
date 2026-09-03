using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Forces a searchable-PDF successor from a version (#999's "Make searchable") — the user overruling the
/// scanned-PDF detector. A sibling of DocumentVersionsController on the versions route (the ADR 0571
/// recipe), because that controller sits at its size ceiling and this is its own small responsibility.
/// </summary>
/// <remarks>
/// POST on an action sub-resource, deliberately: forcing a conversion is a genuine transition, not a
/// create/replace/delete of anything addressable. The conversion itself runs off the request path — the
/// worker picks up the forced outbox row, skips detection, and files the successor; languages come from
/// the version's override (set via the ocr-languages PUT) else the tenant default, unchanged precedence
/// (ADR 0272).
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/versions/{versionId:guid}/searchable")]
[Authorize]
public class DocumentSearchableController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly ISearchablePdfQueue _searchablePdfQueue;
    private readonly IAuditRecorder _audit;

    public DocumentSearchableController(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        ISearchablePdfQueue searchablePdfQueue,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _access = access;
        _searchablePdfQueue = searchablePdfQueue;
        _audit = audit;
    }

    /// <summary>The one predicate the emitter and this enforcer share (ADR 0543).</summary>
    internal static bool IsOcrCandidate(string objectKey) =>
        Path.GetExtension(objectKey).ToLowerInvariant() is ".tif" or ".tiff" or ".pdf";

    [HttpPost]
    public async Task<IActionResult> MakeSearchable(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var documentName = await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken);
        if (documentName is null)
        {
            return NotFound();
        }

        // Creating a successor version is a content edit — the same gate as filing a version.
        if (!await _access.CanEditContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        var version = await _dbContext.DocumentVersions
            .SingleOrDefaultAsync(v => v.Id == versionId && v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed, cancellationToken);
        if (version is null || !IsOcrCandidate(version.ObjectKey))
        {
            return NotFound(); // matches the withheld rel: this version has no searchable surface.
        }

        if (version.IsSigned == true)
        {
            // OCR would break the signature — the rel was withheld, so only a non-conforming caller lands here.
            throw new Errors.Exceptions.Documents.SignedVersionNotConvertibleException();
        }

        await _searchablePdfQueue.EnqueueAsync(documentId, versionId, force: true, cancellationToken: cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentOcrForced, "Document", documentId, documentName,
            $"Searchable-PDF conversion forced for version {version.VersionNumber}", cancellationToken: cancellationToken);

        // 202: the successor arrives when the worker has run — the caller polls the versions list it
        // already reads, and the verdict line explains any refusal.
        return Accepted();
    }
}
