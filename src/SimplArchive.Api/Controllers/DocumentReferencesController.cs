using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.References;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

using SimplArchive.Api.Documents;
using SimplArchive.Infrastructure.Masks;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// See ADR "Desktop drag-and-drop move and reference". A reference is a shortcut that files
/// an existing document/folder into another folder without changing its real home (Document.ParentId). This
/// controller lists/creates/removes references filed in a folder. References are exposed on their own list
/// endpoint (not merged into /children), so the existing children listing/pagination/resource shape is
/// untouched — the desktop loads children + references and shows both. GET/HEAD require CanSee on the
/// folder; POST requires CanCreateSubItems on the folder + CanSee on the target; DELETE requires
/// CanCreateSubItems on the folder. Removing a reference removes only the shortcut, never the target.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{folderId:guid}/references")]
[Authorize]
public class DocumentReferencesController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IMaskContainmentProvider _containment;

    public DocumentReferencesController(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor currentTenantAccessor,
        IMaskContainmentProvider containment,
        IAuditRecorder audit,
        Documents.DocumentAccessService access)
    {
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
        _containment = containment;
        _audit = audit;
        _access = access;
    }

    private readonly Documents.DocumentAccessService _access;
    private readonly IAuditRecorder _audit;

    // Plain mutable classes, not records — XmlSerializer (ADR "JSON/XML content negotiation") needs a
    // parameterless constructor and settable properties.
    public class ReferenceResource : HypermediaResource
    {
        public Guid ReferenceId { get; set; }

        // The referenced item (target). Named Id (not TargetId) so a reference row and a child document row
        // read the same way client-side.
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public bool HasChildren { get; set; }

        public bool HasVersions { get; set; }

        public bool HasSubfolders { get; set; }

        // True when at least one other DocumentReference targets this item — see ADR "References-of-an-item
        // list". Lets a reference row offer "References …" on its target too.
        public bool HasReferences { get; set; }

        // The target's real home folder (Document.ParentId) — null if the target is a repository root. Drives
        // the client's "Go to …" navigation.
        public Guid? RealParentId { get; set; }

        // Always true — lets the client render the shortcut icon and reference-specific context menu without
        // inferring it from the endpoint.
        public bool IsReference { get; set; } = true;

        // The TARGET's list-row columns, exactly as a child row carries them (#768). A reference is another
        // appearance of a document, so its row is the same row — these were absent, and the contents list drew
        // blank Type / Doc date / Size / Tags cells for every referenced item while its columns worked
        // perfectly for children.
        public string FileExtension { get; set; } = "";

        public string DocumentType { get; set; } = "";

        public DateOnly? DocumentDate { get; set; }

        public long? SizeBytes { get; set; }

        public List<string> Tags { get; set; } = [];

        public string SensitivityLabelName { get; set; } = "";

        public string? SensitivityLabelColor { get; set; }

        public int VersionCount { get; set; }

        public DateTimeOffset? VersionCreatedAt { get; set; }

        public string? Icon { get; set; }

        /// <inheritdoc cref="DocumentChildrenController.DocumentSummaryResource.CreatedBy"/>
        public string CreatedBy { get; set; } = "";
    }

    public class ReferenceListResource : HypermediaResource
    {
        public List<ReferenceResource> References { get; set; } = [];
    }

    public class CreateReferenceRequest
    {
        public Guid TargetId { get; set; }
    }

    private record ReferenceRow(
        Guid ReferenceId, DateTimeOffset CreatedAt, Guid TargetId, string Name,
        bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, Guid? RealParentId);

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted CreatedAt
    // ascending, Id ascending as tiebreaker. A reference whose target is soft-deleted is excluded (the join
    // to Documents applies the soft-delete query filter), and reappears when the target is restored.
    [HttpGet]
    public async Task<IActionResult> List(Guid folderId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == folderId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(folderId, cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.DocumentReferences
            .Where(r => r.ParentFolderId == folderId && _dbContext.Documents.Any(d => d.Id == r.TargetDocumentId));

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(r => r.CreatedAt > cursorCreatedAt || (r.CreatedAt == cursorCreatedAt && r.Id > cursorId));
        }

        // The presentation booleans are computed against the TARGET (a reference to a folder shows the
        // folder icon and drills into the folder's children), mirroring DocumentsController.ListChildren.
        var fetched = await query
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Take(pageSize + 1)
            .Select(r => new ReferenceRow(
                r.Id,
                r.CreatedAt,
                r.TargetDocumentId,
                _dbContext.Documents.Where(d => d.Id == r.TargetDocumentId).Select(d => d.Name).FirstOrDefault()!,
                // Anything filed here at all: a child document/subfolder, or a REFERENCE filed into it (issue
                // #376). A folder holding only shortcuts still has contents the list shows.
                _dbContext.Documents.Any(c => c.ParentId == r.TargetDocumentId)
                    || _dbContext.DocumentReferences.Any(x => x.ParentFolderId == r.TargetDocumentId),
                _dbContext.DocumentVersions.Any(v => v.DocumentId == r.TargetDocumentId),
                _dbContext.Documents.Any(c => c.ParentId == r.TargetDocumentId && !_dbContext.DocumentVersions.Any(v => v.DocumentId == c.Id)),
                _dbContext.DocumentReferences.Any(other => other.TargetDocumentId == r.TargetDocumentId),
                _dbContext.Documents.Where(d => d.Id == r.TargetDocumentId).Select(d => d.ParentId).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { folderId, cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].ReferenceId);
            links.Add(new Link("next", Url.Action(nameof(List), new { folderId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        // The TARGETS' list-row columns, from the SAME projection a child row uses (#768). One batched query
        // for the page rather than a correlated set per row, and — the point — one definition of what a list
        // row carries, so a column added to children cannot silently skip references.
        var targetIds = page.Select(p => p.TargetId).ToList();
        var columns = (await _dbContext.Documents
                .Where(d => targetIds.Contains(d.Id))
                .AsSummaryRows(_dbContext)
                .ToListAsync(cancellationToken))
            .ToDictionary(r => r.Id);

        var tagsByDoc = await DocumentSummaryQueries.TagsForAsync(_dbContext, targetIds, cancellationToken);
        var rules = await _containment.ForAsync(_dbContext, _currentTenantAccessor.TenantId!.Value, cancellationToken);

        return Ok(new ReferenceListResource
        {
            References = page.Select(row => BuildResource(folderId, row, columns.GetValueOrDefault(row.TargetId), tagsByDoc, rules)).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public async Task<IActionResult> HeadList(Guid folderId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == folderId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(folderId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid folderId, [FromBody] CreateReferenceRequest request, CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Documents
            .Where(d => d.Id == folderId)
            .Select(d => new { d.TenantId })
            .SingleOrDefaultAsync(cancellationToken);

        if (folder is null)
        {
            return NotFound();
        }

        // Target must exist in the caller's tenant (the tenant query filter scopes this lookup).
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == request.TargetId, cancellationToken))
        {
            throw new ReferenceTargetNotFoundException();
        }

        var folderRights = await GetCallerRightsAsync(folderId, cancellationToken);
        var targetRights = await GetCallerRightsAsync(request.TargetId, cancellationToken);

        if (!folderRights.CanCreateSubItems || !targetRights.CanSee)
        {
            return Forbid();
        }

        // Can't reference an item into itself or (for a folder target) into its own subtree — that would make
        // the tree loop forever. The DB CHECK also blocks the exact-self case.
        if (await IsAncestorOrSelfAsync(request.TargetId, folderId, cancellationToken))
        {
            throw new InvalidReferenceTargetException();
        }

        if (await _dbContext.DocumentReferences.AnyAsync(r => r.ParentFolderId == folderId && r.TargetDocumentId == request.TargetId, cancellationToken))
        {
            throw new ReferenceAlreadyExistsException();
        }

        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();

        var reference = new DocumentReference
        {
            Id = Guid.NewGuid(),
            TenantId = folder.TenantId,
            ParentFolderId = folderId,
            TargetDocumentId = request.TargetId,
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.DocumentReferences.Add(reference);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var target = await _dbContext.Documents
            .Where(d => d.Id == request.TargetId)
            .Select(d => new { d.Name, d.ParentId })
            .SingleAsync(cancellationToken);

        await _audit.RecordAsync(AuditActions.ReferenceAdded, "Document", request.TargetId, target.Name, "Reference added", cancellationToken: cancellationToken);

        var row = new ReferenceRow(
            reference.Id, reference.CreatedAt, request.TargetId, target.Name,
            await _dbContext.Documents.AnyAsync(c => c.ParentId == request.TargetId, cancellationToken),
            await _dbContext.DocumentVersions.AnyAsync(v => v.DocumentId == request.TargetId, cancellationToken),
            await _dbContext.Documents.AnyAsync(c => c.ParentId == request.TargetId && !_dbContext.DocumentVersions.Any(v => v.DocumentId == c.Id), cancellationToken),
            await _dbContext.DocumentReferences.AnyAsync(other => other.TargetDocumentId == request.TargetId, cancellationToken),
            target.ParentId);

        // The created reference answers with the same row shape the listing does — a client that files a
        // reference and renders the response must not get a stub where a listed row would have columns.
        var created = (await _dbContext.Documents
            .Where(d => d.Id == request.TargetId)
            .AsSummaryRows(_dbContext)
            .ToListAsync(cancellationToken)).FirstOrDefault();

        return CreatedAtAction(nameof(List), new { folderId }, BuildResource(
            folderId,
            row,
            created,
            await DocumentSummaryQueries.TagsForAsync(_dbContext, [request.TargetId], cancellationToken),
            await _containment.ForAsync(_dbContext, _currentTenantAccessor.TenantId!.Value, cancellationToken)));
    }

    // Removes only the shortcut, never the target. Requires CanCreateSubItems on the containing folder (the
    // right that governs that folder's contents).
    [HttpDelete("{referenceId:guid}")]
    public async Task<IActionResult> Delete(Guid folderId, Guid referenceId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == folderId, cancellationToken))
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(folderId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        var reference = await _dbContext.DocumentReferences
            .SingleOrDefaultAsync(r => r.Id == referenceId && r.ParentFolderId == folderId, cancellationToken);

        if (reference is null)
        {
            return NotFound();
        }

        _dbContext.DocumentReferences.Remove(reference);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var targetName = await _dbContext.Documents.Where(d => d.Id == reference.TargetDocumentId).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.ReferenceRemoved, "Document", reference.TargetDocumentId, targetName, "Reference removed", cancellationToken: cancellationToken);

        return NoContent();
    }

    private static ReferenceResource BuildResource(
        Guid folderId, ReferenceRow row, DocumentSummaryRow? columns, IReadOnlyDictionary<Guid, List<string>> tagsByDoc, MaskContainmentRules rules)
    {
        // A reference row stands for a REAL document, so it advertises the same unconditional target
        // sub-resources a children row does (issue #416) — without them, a client that selects a reference has
        // the target's address but none of its collections, and either special-cases the row (a fetch per rel)
        // or quietly offers it less than the real row beside it. The row's own affordances (`delete`, `go-to`)
        // ride alongside. Conditional rels stay off for the children-listing's stated reason: a listing is the
        // wrong place to answer "may I?".
        var links = new List<Link>
        {
            new("delete", $"/api/documents/{folderId}/references/{row.ReferenceId}", "DELETE"),
            new("self", $"/api/documents/{row.TargetId}", "GET"),
            new("chat", $"/api/documents/{row.TargetId}/chat", "GET"),
            new("versions", $"/api/documents/{row.TargetId}/versions", "GET"),
            new("children", $"/api/documents/{row.TargetId}/children", "GET"),
            new("mask", $"/api/documents/{row.TargetId}/mask", "GET"),
            new("index-data", $"/api/documents/{row.TargetId}/index-data", "GET"),
            new("references", $"/api/documents/{row.TargetId}/references", "GET"),
            new("referencing-folders", $"/api/documents/{row.TargetId}/referencing-folders", "GET"),
        };

        if (row.RealParentId is { } realParentId)
        {
            links.Add(new Link("go-to", $"/api/documents/{realParentId}", "GET"));
        }

        return new ReferenceResource
        {
            ReferenceId = row.ReferenceId,
            Id = row.TargetId,
            Name = row.Name,
            HasChildren = row.HasChildren,
            HasVersions = row.HasVersions,
            HasSubfolders = row.HasSubfolders,
            HasReferences = row.HasReferences,
            RealParentId = row.RealParentId,
            IsReference = true,

            // The target's columns. Absent only if the target vanished between the two queries, in which case
            // the row keeps its defaults rather than failing the whole listing.
            FileExtension = Path.GetExtension(columns?.LatestObjectKey ?? ""),
            DocumentType = columns?.DocumentType ?? "",
            DocumentDate = columns?.DocumentDate,
            SizeBytes = columns?.SizeBytes,
            Tags = tagsByDoc.TryGetValue(row.TargetId, out var tags) ? tags : [],
            SensitivityLabelName = columns?.SensitivityLabelName ?? "",
            SensitivityLabelColor = columns?.SensitivityLabelColor,
            VersionCount = columns?.VersionCount ?? 0,
            VersionCreatedAt = columns?.VersionCreatedAt,
            Icon = columns is null ? null : rules.IconOf(columns.MaskId),
            CreatedBy = columns?.CreatedByName ?? "",
            Links = links,
        };
    }

    // Walks up from startId via ParentId; true if candidateAncestorId is startId itself or any of its
    // ancestors. Rejects referencing an item into itself or its own subtree.
    private async Task<bool> IsAncestorOrSelfAsync(Guid candidateAncestorId, Guid startId, CancellationToken cancellationToken)
    {
        Guid? currentId = startId;

        while (currentId is { } id)
        {
            if (id == candidateAncestorId)
            {
                return true;
            }

            currentId = await _dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => d.ParentId)
                .SingleAsync(cancellationToken);
        }

        return false;
    }

    // Checks ServiceAccount first, then a logged-in User — the two accessors are mutually exclusive per
    // request. See ADR "Document-scope authorization retrofit for User".
    private Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken) =>
        _access.GetCallerRightsAsync(documentId, cancellationToken);

    private async Task<bool> CanSeeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanSee;
    }

    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity() => _access.GetCallerIdentity();
}
