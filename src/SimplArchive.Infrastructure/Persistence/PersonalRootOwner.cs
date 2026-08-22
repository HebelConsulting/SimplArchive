using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SimplArchive.Domain.Documents;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Keeps <see cref="Document.PersonalRootOwnerId"/> equal to the row's root's <c>PersonalOfUserId</c>
/// (ADR 0670), so a rights check can ask "is this inside somebody else's personal space?" with one column read
/// instead of a walk to the root.
/// </summary>
/// <remarks>
/// <para>
/// Driven from <c>SimplArchiveDbContext.SaveChanges</c> for the reason every other document invariant is: a
/// parent is changed by the workbench, WebDAV, CalDAV/CardDAV, IMAP, import, filing from the intray and by
/// promoting a reference to a primary location — a derived column maintained in one of those is a column the
/// other six leave lying. Unlike the placement RULES, this does no I/O beyond the change tracker and its own
/// table, which is what makes SaveChanges the right home for it rather than the wrong one.
/// </para>
/// <para>
/// A static helper taking the context rather than a member of it, on the <see cref="Acl.GroupMembershipExpansion"/>
/// precedent: the DbContext is on the over-limit debt list, and this is a cohesive responsibility of its own.
/// </para>
/// </remarks>
public static class PersonalRootOwner
{
    /// <summary>Derives the column for one changed document, and rewrites its subtree if the answer moved.</summary>
    public static async Task MaintainAsync(
        SimplArchiveDbContext dbContext,
        Document document,
        Dictionary<Guid, Document> trackedDocuments,
        CancellationToken cancellationToken)
    {
        var entry = dbContext.Entry(document);

        // Only a new row, a re-parenting, or a root gaining/losing its owner can change the answer. Every other
        // modification — a rename, a mask change, a soft delete — leaves it alone, which is what keeps this off
        // the cost of ordinary edits.
        if (!MayHaveChanged(entry))
        {
            return;
        }

        var owner = await DeriveAsync(dbContext, document, trackedDocuments, cancellationToken);
        if (owner == document.PersonalRootOwnerId)
        {
            return;
        }

        document.PersonalRootOwnerId = owner;

        // A new row has no descendants yet; anything else may be a subtree that just moved into or out of a
        // personal space, and every row under it now answers differently.
        if (entry.State != EntityState.Added)
        {
            await RewriteDescendantsAsync(dbContext, document.Id, owner, cancellationToken);
        }
    }

    private static bool MayHaveChanged(EntityEntry<Document> entry) =>
        entry.State == EntityState.Added
        || entry.Property(d => d.ParentId).IsModified
        || entry.Property(d => d.PersonalOfUserId).IsModified;

    // Walks to the root through the CHANGE TRACKER, falling back to the stored column at the first ancestor that
    // isn't tracked. Deliberately order-independent: provisioning inserts a personal root and its folders in one
    // SaveChanges, so a child is routinely reached before its own parent has been derived — reading the parent's
    // still-null derived column would file the whole personal space as "not personal".
    private static async Task<Guid?> DeriveAsync(
        SimplArchiveDbContext dbContext,
        Document document,
        Dictionary<Guid, Document> trackedDocuments,
        CancellationToken cancellationToken)
    {
        var current = document;
        var visited = new HashSet<Guid>();

        while (true)
        {
            if (current.PersonalOfUserId is { } own)
            {
                return own;
            }

            // A root, or a cycle — which DetectDocumentCycleAndCrossTenantParentAsync rejects for real; the
            // guard is here only so a malformed tracker graph can't spin instead of reaching that rejection.
            if (current.ParentId is not { } parentId || !visited.Add(current.Id))
            {
                return null;
            }

            if (trackedDocuments.TryGetValue(parentId, out var trackedParent))
            {
                current = trackedParent;
                continue;
            }

            return await dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
                .Where(d => d.Id == parentId)
                .Select(d => d.PersonalRootOwnerId)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }

    // Level-by-level rather than a recursive CTE, because the rows must join THIS SaveChanges: an ExecuteUpdate
    // would run immediately and survive a later failure, leaving the column describing a move that never
    // happened. Loading them instead means the rewrite commits or rolls back with the move itself.
    //
    // Soft-deleted descendants are included on purpose — one restored from the recycle bin after its ancestor
    // moved would otherwise carry the answer from where it used to live.
    private static async Task RewriteDescendantsAsync(
        SimplArchiveDbContext dbContext, Guid documentId, Guid? owner, CancellationToken cancellationToken)
    {
        var frontier = new List<Guid> { documentId };

        while (frontier.Count > 0)
        {
            var children = await dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
                .Where(d => d.ParentId != null && frontier.Contains(d.ParentId.Value))
                .ToListAsync(cancellationToken);

            foreach (var child in children)
            {
                child.PersonalRootOwnerId = owner;
            }

            frontier = [.. children.Select(c => c.Id)];
        }
    }
}
