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
    /// Heals the log against the collection's ACTUAL state before it is answered from (#806) — the belt over
    /// the SaveChanges recorder's braces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recorder covers every path that goes through this DbContext; this covers everything else — a raw
    /// SQL migration, a path added later that someone routes around the context, the log rows that predate
    /// the recorder existing at all. An item whose current version is newer than its last log entry is
    /// re-announced; a logged item that no longer lives here is announced removed. Runs on the sync/CTag
    /// reads, where a stale log is about to become a stale ANSWER — collections are small (a person's
    /// contacts, a calendar), so the two queries are cheap where they matter.
    /// </para>
    /// <para>
    /// Deliberately rights-INDEPENDENT: the log describes the collection, not any one viewer. Reconciling
    /// through a restricted caller's view would record removals for items they merely cannot see.
    /// </para>
    /// </remarks>
    internal static async Task ReconcileAsync(
        SimplArchiveDbContext db, DavProtocol protocol, Guid tenantId, Guid folderId, CancellationToken cancellationToken)
    {
        var items = await db.Documents
            .Where(d => d.ParentId == folderId && d.MaskVersionId != null
                && db.MaskVersions.Any(v => v.Id == d.MaskVersionId && protocol.ItemMaskIds.Contains(v.MaskId)))
            .Select(d => new
            {
                d.Id,
                NewestVersionAt = db.DocumentVersions
                    .Where(v => v.DocumentId == d.Id && v.Status == Domain.Documents.DocumentVersionStatus.Confirmed)
                    .Max(v => (DateTimeOffset?)v.CreatedAt),
                Uid = db.FieldValues
                    .Where(fv => fv.DocumentId == d.Id
                        && db.FieldDefinitions.Any(f => f.Id == fv.FieldDefinitionId
                            && f.Name == protocol.UidFieldName
                            && db.MaskVersions.Any(mv => mv.Id == f.MaskVersionId && protocol.ItemMaskIds.Contains(mv.MaskId))))
                    .Select(fv => fv.Value)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var logged = (await db.DavCollectionChanges
            .Where(c => c.FolderId == folderId)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken))
            .GroupBy(c => c.DocumentId)
            .ToDictionary(g => g.Key, g => g.Last());

        foreach (var item in items.Where(i => i.NewestVersionAt is not null))
        {
            var known = logged.GetValueOrDefault(item.Id);
            if (known is null || known.ChangeType == DavChangeType.Removed || item.NewestVersionAt > known.At)
            {
                await RecordAsync(db, tenantId, folderId, item.Id,
                    (item.Uid is { Length: > 0 } uid ? uid : item.Id.ToString()) + protocol.Extension,
                    DavChangeType.Modified, cancellationToken);
            }
        }

        var present = items.Where(i => i.NewestVersionAt is not null).Select(i => i.Id).ToHashSet();
        foreach (var (documentId, last) in logged)
        {
            if (last.ChangeType != DavChangeType.Removed && !present.Contains(documentId))
            {
                await RecordAsync(db, tenantId, folderId, documentId, last.ResourceName, DavChangeType.Removed, cancellationToken);
            }
        }
    }

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
