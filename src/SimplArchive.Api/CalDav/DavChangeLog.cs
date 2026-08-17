// The append-only change log behind CTag and RFC 6578 sync-collection (#564 slice 3, ADR 0622). Recorded at
// the DAV write path rather than in SaveChanges: a change is only interesting to a sync client once it has a
// RESOURCE NAME, and that name is derived from the item's UID, which classification fills in after the row
// exists — a DbContext-level hook would fire before the item is addressable.
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.CalDav;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

internal static class DavChangeLog
{
    /// <summary>Records one change and returns the new sequence value (the collection's CTag).</summary>
    internal static async Task<long> RecordAsync(
        SimplArchiveDbContext db, Guid tenantId, Guid folderId, Guid documentId, string resourceName,
        DavChangeType changeType, CancellationToken cancellationToken)
    {
        var change = new DavCollectionChange
        {
            TenantId = tenantId,
            FolderId = folderId,
            DocumentId = documentId,
            ResourceName = resourceName,
            ChangeType = changeType,
            At = DateTimeOffset.UtcNow,
        };

        db.DavCollectionChanges.Add(change);
        await db.SaveChangesAsync(cancellationToken);
        return change.Id;
    }

    /// <summary>
    /// The collection's current sequence — its CTag, and the sync-token a client is handed. Zero when nothing
    /// has changed yet, which is a valid starting token: everything is then "since 0".
    /// </summary>
    internal static async Task<long> CurrentAsync(SimplArchiveDbContext db, Guid folderId, CancellationToken cancellationToken) =>
        await db.DavCollectionChanges
            .Where(c => c.FolderId == folderId)
            .Select(c => (long?)c.Id)
            .MaxAsync(cancellationToken) ?? 0;

    /// <summary>
    /// What changed after <paramref name="since"/>, newest state per item. An item created and then removed
    /// inside one window is reported ONLY as removed — a client that never saw it does not need its creation,
    /// and telling it about both would have it fetch an href that is already gone.
    /// </summary>
    internal static async Task<List<DavCollectionChange>> SinceAsync(
        SimplArchiveDbContext db, Guid folderId, long since, CancellationToken cancellationToken)
    {
        var changes = await db.DavCollectionChanges
            .Where(c => c.FolderId == folderId && c.Id > since)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);

        return changes
            .GroupBy(c => c.DocumentId)
            .Select(g => g.Last())
            .OrderBy(c => c.Id)
            .ToList();
    }
}
