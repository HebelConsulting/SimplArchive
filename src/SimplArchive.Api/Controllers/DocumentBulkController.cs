using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Bulk actions over a set of selected documents (ADR "Bulk actions on selected documents") — move / delete /
/// add-tags / set-sensitivity applied to many items in one call. Each item is authorized + guarded
/// independently (the same rules as the single-item endpoints); an item the caller can't touch or that is
/// refused (legal hold / check-out / name conflict / cycle) is silently <em>skipped</em>, and the response
/// reports how many succeeded vs skipped. Accepts either a ServiceAccount or a User caller.
/// </summary>
/// <remarks>
/// <para>
/// NO CONCURRENCY CHECK, by the owner's decision (2026-09-14, #1175/#1172) — and that is a decision rather
/// than an omission, so it is written here where the next person will look for it.
/// </para>
/// <para>
/// A bulk request names a SET and carries at most ONE <c>If-Match</c>. Honouring it would mean requiring that
/// single token to match every document in the set, which is not a precondition — it is a coincidence, and it
/// would refuse the whole batch because somebody else renamed one unrelated item. The alternatives were
/// available and were not taken: a token per id in the body, or a collection-level CTag.
/// </para>
/// <para>
/// The per-item <c>SaveChanges</c> below is deliberate for the same reason. Each iteration skips what the
/// caller may not touch and the response reports per-item outcomes (ADR 0797), which is the contract callers
/// rely on. Do NOT wrap the loop in one transaction to satisfy the one-action-one-transaction rule (ADR 0794):
/// that rule is about ONE user action on ONE object, and applying it here would convert a partial success into
/// a total failure — the opposite of what a bulk action promises.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/bulk")]
[Authorize]
public class DocumentBulkController : ControllerBase
{
    // A defensive cap; the clients only ever send the current on-screen selection.
    private const int MaxItems = 500;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ILegalHoldService _legalHold;
    private readonly IDocumentIndexQueue _queue;
    private readonly IAuditRecorder _audit;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly Documents.DocumentMover _mover;
    private readonly SimplArchive.Application.Abstractions.IObjectStorageClient _objectStorage;
    private readonly Documents.DocumentAccessService _access;

    public DocumentBulkController(
        SimplArchiveDbContext dbContext,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        ILegalHoldService legalHold,
        IDocumentIndexQueue queue,
        IAuditRecorder audit,
        IUserSystemRightsResolver userSystemRights,
        Documents.DocumentMover mover,
        SimplArchive.Application.Abstractions.IObjectStorageClient objectStorage,
        Documents.DocumentAccessService access)
    {
        _objectStorage = objectStorage;
        _mover = mover;
        _dbContext = dbContext;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _legalHold = legalHold;
        _queue = queue;
        _audit = audit;
        _userSystemRights = userSystemRights;
        _access = access;
    }

    public class BulkExportRequest
    {
        public List<Guid> Ids { get; set; } = [];
        public string? Name { get; set; }
    }

    /// <summary>
    /// One combined file from a uniform .vcf or .ics selection (#658): every consuming application expects a
    /// stream of records, not thirty files imported thirty times. STRICT, unlike the bulk mutations above: a
    /// skipped item there is one move fewer, a skipped item HERE is a file that silently lacks a contact —
    /// serving something else is worse than refusing, so any unreadable or non-combinable item refuses the
    /// whole request with the reason.
    /// </summary>
    // STAYS POST (ADR 0797). It IS a read, which is the honest objection — but the thing being read is
    // identified by N document ids, and a GET carries them only in the query string, which servers and proxies
    // length-limit. A read whose selection does not fit in a URL is the textbook case for POST.
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] BulkExportRequest request, CancellationToken cancellationToken)
    {
        var ids = Distinct(request.Ids);
        if (ids.Count == 0 || ids.Count > MaxItems)
        {
            return BadRequest();
        }

        var payloads = new List<byte[]>();
        string? extension = null;
        foreach (var id in ids)
        {
            if (await GetDocumentAsync(id, cancellationToken) is not { } document
                || !(await GetCallerRightsAsync(id, cancellationToken)).CanReadContent)
            {
                return NotFound();
            }

            var version = await Infrastructure.Persistence.CurrentVersion.ResolveAsync(
                _dbContext.DocumentVersions, id, document.CurrentVersionId, cancellationToken);
            if (version?.ObjectKey is not { Length: > 0 } objectKey)
            {
                throw new Errors.Exceptions.Documents.BulkExportNotCombinableException();
            }

            var itemExtension = Path.GetExtension(objectKey);
            if (itemExtension is not (".vcf" or ".ics") || (extension is not null && itemExtension != extension))
            {
                throw new Errors.Exceptions.Documents.BulkExportNotCombinableException();
            }

            extension = itemExtension;
            await using var stream = await _objectStorage.GetObjectAsync(objectKey, cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            payloads.Add(buffer.ToArray());
        }

        var combined = extension == ".vcf"
            ? Documents.CombinedItemExport.CombineVcf(payloads)
            : Documents.CombinedItemExport.CombineIcs(payloads);
        var stem = string.IsNullOrWhiteSpace(request.Name) ? "export" : request.Name.Trim();
        return File(combined, extension == ".vcf" ? "text/vcard" : "text/calendar", $"{stem}{extension}");
    }

    public class BulkMoveRequest
    {
        public List<Guid> Ids { get; set; } = [];
        public Guid ParentId { get; set; }
    }

    public class BulkReferenceRequest
    {
        public List<Guid> Ids { get; set; } = [];
        public Guid ParentId { get; set; } // the folder the references are filed into
    }

    public class BulkDeleteRequest
    {
        public List<Guid> Ids { get; set; } = [];
    }

    public class BulkTagsRequest
    {
        public List<Guid> Ids { get; set; } = [];
        public List<string> Tags { get; set; } = [];
    }

    public class BulkSensitivityRequest
    {
        public List<Guid> Ids { get; set; } = [];
        public Guid? LabelId { get; set; }
    }

    public class BulkResultResource : HypermediaResource
    {
        public int Succeeded { get; set; }
        public int Skipped { get; set; }
    }

    // Move every selected item into one target folder. The target's existence + the caller's CanCreateSubItems
    // on it are validated once (a bad target fails the whole call); each item then needs its own CanMove, must
    // not be frozen / checked out by another, and must not create a cycle or a sibling-name clash — else skipped.
    // The batch index (issue #416). These five operations act on a SET of ids, so no single resource owns
    // them and — until this existed — nothing linked to any of them: the route answered five POSTs and no GET,
    // which is the same shape that left `/api/admin` unreachable. A client is meant to be able to follow what
    // it is offered, so a batch endpoint needs somewhere to be offered FROM.
    //
    // A resource of its own rather than five names on the API root (the root is the one document every client
    // fetches, and it should not become the home for everything that fits nowhere else) and rather than five
    // links on every children listing (a selection can span listings — search results, the recycle bin — and
    // every listing response would carry them whether or not anything is selected).
    //
    // Deliberately not right-gated: this says what exists, and each operation enforces its own permissions per
    // document when followed — the same contract as the API root itself.
    [HttpGet]
    public IActionResult Index() => Ok(new BulkIndexResource
    {
        Links =
        [
            new Link("self", "/api/documents/bulk", "GET"),
            new Link("move", "/api/documents/bulk/parent", "PUT"),
            new Link("reference", "/api/documents/bulk/references", "POST"),
            new Link("delete", "/api/documents/bulk", "DELETE"),
            new Link("tags", "/api/documents/bulk/tags", "POST"),
            new Link("sensitivity", "/api/documents/bulk/sensitivity", "PUT"),
            new Link("export", "/api/documents/bulk/export", "POST"),
        ],
    });

    [HttpHead]
    public IActionResult IndexHead() => NoContent();

    public class BulkIndexResource : HypermediaResource;

    [HttpPut("parent")]
    public async Task<IActionResult> Move([FromBody] BulkMoveRequest request, CancellationToken cancellationToken)
    {
        var ids = Distinct(request.Ids);
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == request.ParentId, cancellationToken))
        {
            throw new MoveTargetNotFoundException();
        }

        if (!(await GetCallerRightsAsync(request.ParentId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        // Moving a root document (a repository) into a folder demotes the repository — needs CanManageRepositories
        // (ADR "Repository creation endpoint"), resolved once for the caller. A root item is skipped without it.
        var hasManageRepositories = await HasManageRepositoriesRightAsync(cancellationToken);

        var succeeded = 0;
        var skipped = 0;
        foreach (var id in ids)
        {
            if (id == request.ParentId
                || await GetDocumentAsync(id, cancellationToken) is not { } document
                || !(await GetCallerRightsAsync(id, cancellationToken)).CanMove
                || (document.ParentId is null && !hasManageRepositories)
                || await _legalHold.IsFrozenAsync(id, cancellationToken)
                || await IsCheckedOutByOtherAsync(id, cancellationToken)
                || await IsAncestorOrSelfAsync(id, request.ParentId, cancellationToken))
            {
                skipped++;
                continue;
            }

            // Filing out of a mail inbox moves the bytes too (#633). Inside the per-item try, because a bulk
            // move SKIPS what it cannot do rather than failing the batch — a refused crossing is one skipped
            // item, exactly like a name clash below it.
            try
            {
                await _mover.RelocateContentForMoveAsync(id, request.ParentId, cancellationToken);
            }
            catch (Errors.Exceptions.Documents.CannotFileIntoEphemeralMailException)
            {
                skipped++;
                continue;
            }

            document.ParentId = request.ParentId;
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
            {
                _dbContext.Entry(document).State = EntityState.Unchanged; // a name clash — leave it where it was
                skipped++;
                continue;
            }

            await _queue.EnqueueAsync(id, cancellationToken);
            await _audit.RecordAsync(AuditActions.DocumentMoved, "Document", id, document.Name, cancellationToken: cancellationToken);
            succeeded++;
        }

        return Ok(Result(succeeded, skipped));
    }

    // Reference every selected item into one target folder (a shortcut, ADR "Desktop drag-and-drop move and
    // reference") — the bulk mirror of POST /api/documents/{folderId}/references. The target folder's existence +
    // CanCreateSubItems are validated once; each item needs CanSee, must not reference into itself / the folder's own
    // subtree, and must not already be referenced there — else skipped. No repository right is needed (a reference
    // leaves the item where it is, unlike a move).
    [HttpPost("references")]
    public async Task<IActionResult> Reference([FromBody] BulkReferenceRequest request, CancellationToken cancellationToken)
    {
        var ids = Distinct(request.Ids);
        var folder = await _dbContext.Documents
            .Where(d => d.Id == request.ParentId).Select(d => new { d.TenantId }).SingleOrDefaultAsync(cancellationToken);
        if (folder is null)
        {
            throw new MoveTargetNotFoundException();
        }

        if (!(await GetCallerRightsAsync(request.ParentId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();
        var succeeded = 0;
        var skipped = 0;
        foreach (var id in ids)
        {
            if (id == request.ParentId
                || await GetDocumentAsync(id, cancellationToken) is not { } document
                || !(await GetCallerRightsAsync(id, cancellationToken)).CanSee
                || await IsAncestorOrSelfAsync(id, request.ParentId, cancellationToken)
                || await _dbContext.DocumentReferences.AnyAsync(r => r.ParentFolderId == request.ParentId && r.TargetDocumentId == id, cancellationToken))
            {
                skipped++;
                continue;
            }

            var reference = new DocumentReference
            {
                Id = Guid.NewGuid(),
                TenantId = folder.TenantId,
                ParentFolderId = request.ParentId,
                TargetDocumentId = id,
                CreatedByUserId = createdByUserId,
                CreatedByServiceAccountId = createdByServiceAccountId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _dbContext.DocumentReferences.Add(reference);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
            {
                _dbContext.Entry(reference).State = EntityState.Detached; // a race lost the uniqueness — leave it out
                skipped++;
                continue;
            }

            await _audit.RecordAsync(AuditActions.ReferenceAdded, "Document", id, document.Name, "Reference added", cancellationToken: cancellationToken);
            succeeded++;
        }

        return Ok(Result(succeeded, skipped));
    }

    // Soft-delete every selected item (each cascading its whole subtree) to the recycle bin. An item needing
    // CanDelete, or whose subtree is under a legal hold / checked out by another, is skipped.
    [HttpDelete]
    public async Task<IActionResult> Delete([FromBody] BulkDeleteRequest request, CancellationToken cancellationToken)
    {
        var succeeded = 0;
        var skipped = 0;
        foreach (var id in Distinct(request.Ids))
        {
            if (await GetDocumentAsync(id, cancellationToken) is not { } document
                || !(await GetCallerRightsAsync(id, cancellationToken)).CanDelete)
            {
                skipped++;
                continue;
            }

            var subtree = await CollectSubtreeAsync(id, document, cancellationToken);
            if (await _legalHold.IsFrozenAsync(id, cancellationToken)
                || await _legalHold.AnyDirectlyHeldAsync(subtree.Select(d => d.Id).ToList(), cancellationToken)
                || subtree.Any(d => d.CheckedOutByUserId is { } h && h != _currentUserAccessor.UserId))
            {
                skipped++;
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var doc in subtree)
            {
                doc.DeletedAt = now;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            foreach (var doc in subtree)
            {
                await _queue.EnqueueAsync(doc.Id, cancellationToken);
            }

            await _audit.RecordAsync(AuditActions.DocumentDeleted, "Document", id, document.Name,
                subtree.Count > 1 ? $"cascade: {subtree.Count} items" : null, cancellationToken: cancellationToken);
            succeeded++;
        }

        return Ok(Result(succeeded, skipped));
    }

    // Add one or more tags to every selected document (union — keeps existing tags; ADR "Document tags"). An
    // item needing CanEditIndexData is skipped. Adding a tag a document already carries is a no-op for it.
    //
    // STAYS POST while its siblings became PUT/DELETE (ADR 0797), and that is the correct shape rather than a
    // leftover: this ADDS to a set, and POST to a collection is exactly how "add to this" is spelled. A PUT
    // would claim the request states the full tag set — which it does not — and would invite a caller to erase
    // somebody's tags by omitting them.
    [HttpPost("tags")]
    public async Task<IActionResult> AddTags([FromBody] BulkTagsRequest request, CancellationToken cancellationToken)
    {
        var tags = (request.Tags ?? [])
            .Select(t => (t ?? "").Trim().ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= 100)
            .Distinct()
            .ToList();

        var succeeded = 0;
        var skipped = 0;
        foreach (var id in Distinct(request.Ids))
        {
            if (await GetDocumentAsync(id, cancellationToken) is not { } document
                || !(await GetCallerRightsAsync(id, cancellationToken)).CanEditIndexData)
            {
                skipped++;
                continue;
            }

            var existing = await _dbContext.DocumentTags.Where(t => t.DocumentId == id).Select(t => t.Tag).ToListAsync(cancellationToken);
            var toAdd = tags.Where(t => !existing.Contains(t)).ToList();
            if (toAdd.Count == 0)
            {
                succeeded++; // nothing to add for this document, but it was a valid target
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var tag in toAdd)
            {
                _dbContext.DocumentTags.Add(new DocumentTag { Id = Guid.NewGuid(), TenantId = document.TenantId, DocumentId = id, Tag = tag, CreatedAt = now });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await _queue.EnqueueAsync(id, cancellationToken);
            await _audit.RecordAsync(AuditActions.DocumentTagsUpdated, "Document", id, document.Name,
                $"Tags added: {string.Join(", ", toAdd)}", cancellationToken: cancellationToken);
            succeeded++;
        }

        return Ok(Result(succeeded, skipped));
    }

    // Set the sensitivity label on every selected document (ADR "Data classification / sensitivity labels"). An
    // item needing CanEditIndexData, or frozen / checked out by another, is skipped.
    [HttpPut("sensitivity")]
    public async Task<IActionResult> SetSensitivity([FromBody] BulkSensitivityRequest request, CancellationToken cancellationToken)
    {
        string? labelName = null;
        if (request.LabelId is { } labelId)
        {
            labelName = await _dbContext.SensitivityLabelDefinitions
                .Where(l => l.Id == labelId && l.RetiredAt == null)
                .Select(l => l.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (labelName is null)
            {
                throw new InvalidSensitivityLabelException();
            }
        }

        var succeeded = 0;
        var skipped = 0;
        foreach (var id in Distinct(request.Ids))
        {
            if (await GetDocumentAsync(id, cancellationToken) is not { } document
                || !(await GetCallerRightsAsync(id, cancellationToken)).CanEditIndexData
                || await _legalHold.IsFrozenAsync(id, cancellationToken)
                || await IsCheckedOutByOtherAsync(id, cancellationToken))
            {
                skipped++;
                continue;
            }

            if (document.SensitivityLabelId != request.LabelId)
            {
                document.SensitivityLabelId = request.LabelId;
                await _dbContext.SaveChangesAsync(cancellationToken);
                await _queue.EnqueueAsync(id, cancellationToken);
                await _audit.RecordAsync(AuditActions.DocumentSensitivityChanged, "Document", id, document.Name, $"Sensitivity set to {labelName ?? "None"}", cancellationToken: cancellationToken);
            }

            succeeded++;
        }

        return Ok(Result(succeeded, skipped));
    }

    private List<Guid> Distinct(List<Guid> ids)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count > MaxItems)
        {
            throw new TooManyBulkItemsException(MaxItems);
        }

        return distinct;
    }

    private static BulkResultResource Result(int succeeded, int skipped) => new() { Succeeded = succeeded, Skipped = skipped };

    private Task<Document?> GetDocumentAsync(Guid id, CancellationToken cancellationToken) =>
        _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    private async Task<bool> IsCheckedOutByOtherAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var holder = await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.CheckedOutByUserId).SingleOrDefaultAsync(cancellationToken);
        return holder is { } h && h != _currentUserAccessor.UserId;
    }

    private async Task<List<Document>> CollectSubtreeAsync(Guid rootId, Document root, CancellationToken cancellationToken)
    {
        var subtree = new List<Document> { root };
        var currentLevelIds = new List<Guid> { rootId };
        while (currentLevelIds.Count > 0)
        {
            var children = await _dbContext.Documents
                .Where(d => d.ParentId != null && currentLevelIds.Contains(d.ParentId!.Value))
                .ToListAsync(cancellationToken);
            if (children.Count == 0)
            {
                break;
            }

            subtree.AddRange(children);
            currentLevelIds = children.Select(c => c.Id).ToList();
        }

        return subtree;
    }

    private async Task<bool> IsAncestorOrSelfAsync(Guid candidateAncestorId, Guid startId, CancellationToken cancellationToken)
    {
        Guid? currentId = startId;
        while (currentId is { } id)
        {
            if (id == candidateAncestorId)
            {
                return true;
            }

            currentId = await _dbContext.Documents.Where(d => d.Id == id).Select(d => d.ParentId).SingleAsync(cancellationToken);
        }

        return false;
    }

    private Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken) =>
        _access.GetCallerRightsAsync(documentId, cancellationToken);

    // The caller's CanManageRepositories system right (User own∪groups, or ServiceAccount) — gates moving a root.
    private async Task<bool> HasManageRepositoriesRightAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageRepositories;
        }

        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts.Where(s => s.Id == serviceAccountId).Select(s => s.CanManageRepositories).SingleOrDefaultAsync(cancellationToken);
        }

        return false;
    }

    // The creating principal for a new DocumentReference — exactly one of user/service-account is set.
    private (Guid? CreatedByUserId, Guid? CreatedByServiceAccountId) GetCallerIdentity() =>
        _currentServiceAccountAccessor.ServiceAccountId is { } saId ? ((Guid?)null, saId) : (_currentUserAccessor.UserId, (Guid?)null);
}
