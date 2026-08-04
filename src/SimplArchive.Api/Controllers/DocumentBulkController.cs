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
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/bulk")]
[Authorize]
public class DocumentBulkController : ControllerBase
{
    // A defensive cap; the clients only ever send the current on-screen selection.
    private const int MaxItems = 500;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ILegalHoldService _legalHold;
    private readonly IDocumentIndexQueue _queue;
    private readonly IAuditRecorder _audit;
    private readonly IUserSystemRightsResolver _userSystemRights;

    public DocumentBulkController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        ILegalHoldService legalHold,
        IDocumentIndexQueue queue,
        IAuditRecorder audit,
        IUserSystemRightsResolver userSystemRights)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _legalHold = legalHold;
        _queue = queue;
        _audit = audit;
        _userSystemRights = userSystemRights;
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
    [HttpPost("move")]
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
    [HttpPost("reference")]
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
    [HttpPost("delete")]
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
    [HttpPost("sensitivity")]
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

    private async Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _effectiveRightsCalculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken);
        }

        return new EffectiveRights(false, false, false, false, false, false, false, false, false);
    }

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
