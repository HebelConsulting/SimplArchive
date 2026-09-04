using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

// Restores a soft-deleted (recycle-bin) document + its cascade-deleted subtree — the inverse of the soft
// delete (ADR "Document delete/restore (recycle bin) implementation"). Shared by DocumentsController (per-item
// POST restore) and RecycleBinController (bulk restore, ADR "Bulk restore from the recycle bin"). Only the
// top-level target may need reparenting to a "Recovered Items" folder (its original parent is gone); cascaded
// descendants keep their ParentId within the subtree being restored together, so they're valid by construction.
// The caller does the CanDelete rights check + the audit — this service is just the restore mechanics.
public sealed class DocumentRestorer
{
    public const string RecoveredItemsFolderName = "Recovered Items";

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IDocumentIndexQueue _indexQueue;
    private readonly DocumentMover _mover;

    public DocumentRestorer(SimplArchiveDbContext dbContext, IDocumentIndexQueue indexQueue, DocumentMover mover)
    {
        _dbContext = dbContext;
        _indexQueue = indexQueue;
        _mover = mover;
    }

    // Restores the given (already-loaded, tracked) soft-deleted document + its deleted subtree. Returns true when
    // it actually restored something; false when the document was already active (an idempotent no-op). The
    // caller identity attributes a freshly-created "Recovered Items" folder.
    public async Task<bool> RestoreAsync(Document document, Guid? callerUserId, Guid? callerServiceAccountId, CancellationToken cancellationToken)
    {
        if (document.DeletedAt is null)
        {
            return false; // already active — nothing to restore
        }

        // If the original parent is no longer a live location (deleted or gone), reparent the top-level target
        // into the repository's "Recovered Items" folder — checked against the normally-filtered DbSet, where
        // "not found" covers both "gone" and "still soft-deleted".
        if (document.ParentId is { } parentId && !await _dbContext.Documents.AnyAsync(d => d.Id == parentId, cancellationToken))
        {
            var rootId = await FindRootIdAsync(parentId, cancellationToken);
            var recoveredId = await GetOrCreateRecoveredItemsFolderIdAsync(rootId, document.TenantId, callerUserId, callerServiceAccountId, cancellationToken);

            // A message whose INBOX is gone lands in Recovered Items, which is archive storage — so its bytes
            // move with it (#633). Rare, and exactly the kind of path a fix confined to the IMAP handler would
            // have missed: nothing here mentions mail.
            await _mover.RelocateContentForMoveAsync(document.Id, recoveredId, cancellationToken);
            document.ParentId = recoveredId;
        }

        var toRestore = await CollectDeletedSubtreeAsync(document, cancellationToken);
        foreach (var doc in toRestore)
        {
            doc.DeletedAt = null;
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Domain.Booking.BookingInvariantException e) when (e.Kind == Domain.Booking.BookingInvariantKind.SlotTaken)
        {
            // Restoring a Room booking is a REBOOKING (ADR 0744): the SaveChanges sync re-activates its
            // claim, and a slot taken since the delete refuses the restore. Translated here — by Kind, not
            // message text — so both restore endpoints report the real cause instead of a blanket 500.
            throw new Errors.Exceptions.Booking.BookingSlotConflictException(e.Message);
        }

        foreach (var doc in toRestore)
        {
            await _indexQueue.EnqueueAsync(doc.Id, cancellationToken);
        }

        return true;
    }

    // The root + every still-soft-deleted descendant (cascade-deleted with it) — a level-by-level walk (not a
    // recursive CTE, to stay provider-agnostic), same as the delete/purge traversals.
    private async Task<List<Document>> CollectDeletedSubtreeAsync(Document root, CancellationToken cancellationToken)
    {
        var subtree = new List<Document> { root };
        var currentLevelIds = new List<Guid> { root.Id };

        while (currentLevelIds.Count > 0)
        {
            var children = await _dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => d.DeletedAt != null && d.ParentId != null && currentLevelIds.Contains(d.ParentId!.Value))
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

    // Walks up from parentId (which may itself be soft-deleted/gone) to the nearest ancestor with ParentId ==
    // null — the root document (what used to be "the repository"). Ignores the soft-delete filter.
    private async Task<Guid> FindRootIdAsync(Guid parentId, CancellationToken cancellationToken)
    {
        var currentId = parentId;
        while (true)
        {
            var parentIdOfCurrent = await _dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => d.Id == currentId)
                .Select(d => d.ParentId)
                .SingleAsync(cancellationToken);

            if (parentIdOfCurrent is not { } nextId)
            {
                return currentId;
            }

            currentId = nextId;
        }
    }

    private async Task<Guid> GetOrCreateRecoveredItemsFolderIdAsync(Guid rootId, Guid tenantId, Guid? callerUserId, Guid? callerServiceAccountId, CancellationToken cancellationToken)
    {
        var existingId = await _dbContext.Documents
            .Where(d => d.ParentId == rootId && d.Name == RecoveredItemsFolderName)
            .Select(d => (Guid?)d.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (existingId is { } id)
        {
            return id;
        }

        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = rootId,
            Name = RecoveredItemsFolderName,
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, cancellationToken),
            CreatedByUserId = callerUserId,
            CreatedByServiceAccountId = callerServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(folder);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return folder.Id;
    }
}
