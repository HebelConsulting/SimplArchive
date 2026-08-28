using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Records CalDAV/CardDAV collection changes at the ONE place every write path passes through (#806):
/// <c>SaveChanges</c>. Before it, the change log was written at the DAV endpoints only — so a contact edited
/// in the workbench, imported, or seeded never reached an already-synced phone: no log entry for the
/// incremental sync, no CTag bump for the poller, no push doorbell. The DAV endpoints still record for
/// themselves; a duplicate row is harmless because the sync answer groups by document and takes the last.
/// </summary>
/// <remarks>
/// <para>
/// Runs BEFORE the base save so the rows commit atomically with the write they describe, and returns them so
/// the context can ring the push doorbell after the commit, when their ids are real.
/// </para>
/// <para>
/// The old objection to a context-level recorder — "the resource name needs the UID, which classification
/// fills in later" — is answered by the same fallback the serving side uses: the document id names the
/// resource until a UID exists, and the save that ADDS the UID field value re-records, so the final name wins
/// in the grouped answer.
/// </para>
/// </remarks>
internal static class DavChangeRecorder
{
    /// <summary>Post-commit doorbell for WebDAV-Push subscribers on writes that did not arrive over DAV.</summary>
    /// <remarks>Best-effort, like the realtime push beside it in the context: the rows are already committed,
    /// and a deaf doorbell loses nothing a poll will not find.</remarks>
    internal static async Task NotifyAsync(
        IDavChangeNotifier? notifier, List<DavCollectionChange> changes, CancellationToken cancellationToken)
    {
        if (notifier is null || changes.Count == 0)
        {
            return;
        }

        foreach (var group in changes.GroupBy(c => c.FolderId))
        {
            try
            {
                await notifier.NotifyAsync(group.Key, group.Max(c => c.Id), cancellationToken);
            }
            catch (Exception)
            {
                // Deliberately swallowed — the mutation must never fail on its announcement.
            }
        }
    }

    internal static async Task<List<DavCollectionChange>> RecordAsync(
        SimplArchiveDbContext db, CancellationToken cancellationToken)
    {
        var tracker = db.ChangeTracker;

        // The cheap gate: nothing DAV-relevant touched, nothing to do — this runs on EVERY save in the system.
        var versionEntries = tracker.Entries<DocumentVersion>().Where(e => e.State == EntityState.Added).ToList();
        var documentEntries = tracker.Entries<Document>().Where(e => e.State == EntityState.Modified).ToList();
        var fieldEntries = tracker.Entries<FieldValue>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
        if (versionEntries.Count == 0 && documentEntries.Count == 0 && fieldEntries.Count == 0)
        {
            return [];
        }

        // (documentId, folderId that must be re-announced, removed-from that folder?) — collected first, so one
        // document touched three ways in one save records once per affected folder.
        var upserts = new Dictionary<(Guid DocumentId, Guid FolderId), bool>(); // value: unused, set semantics
        var removals = new Dictionary<(Guid DocumentId, Guid FolderId), bool>();

        foreach (var entry in versionEntries)
        {
            if (await ParentOfAsync(db, tracker, entry.Entity.DocumentId, cancellationToken) is { } folderId)
            {
                upserts[(entry.Entity.DocumentId, folderId)] = true;
            }
        }

        foreach (var entry in documentEntries)
        {
            var id = entry.Entity.Id;
            var oldParent = entry.Property(d => d.ParentId).OriginalValue;
            var newParent = entry.Property(d => d.ParentId).CurrentValue;
            var oldDeleted = entry.Property(d => d.DeletedAt).OriginalValue;
            var newDeleted = entry.Property(d => d.DeletedAt).CurrentValue;

            if (oldParent != newParent)
            {
                if (oldParent is { } from) { removals[(id, from)] = true; }
                if (newParent is { } to && newDeleted is null) { upserts[(id, to)] = true; }
            }

            if (oldDeleted is null && newDeleted is not null && newParent is { } p1)
            {
                removals[(id, p1)] = true;
                upserts.Remove((id, p1));
            }

            if (oldDeleted is not null && newDeleted is null && newParent is { } p2)
            {
                upserts[(id, p2)] = true;
                removals.Remove((id, p2));
            }
        }

        // The UID definition ids, resolved ONCE per save: they gate the field loop and name pending values,
        // and a per-entry async lookup here would be a query per tracked field on every save in the system.
        var uidDefinitionIds = fieldEntries.Count == 0
            ? new HashSet<Guid>()
            : await UidDefinitionIdsAsync(db, cancellationToken);

        foreach (var entry in fieldEntries)
        {
            // Only a UID field changes the resource NAME; other fields ride along with the version that
            // carries the content change.
            if (uidDefinitionIds.Contains(entry.Entity.FieldDefinitionId)
                && await ParentOfAsync(db, tracker, entry.Entity.DocumentId, cancellationToken) is { } folderId)
            {
                upserts[(entry.Entity.DocumentId, folderId)] = true;
            }
        }

        var changes = new List<DavCollectionChange>();
        foreach (var ((documentId, folderId), _) in removals)
        {
            if (await KindOfFolderAsync(db, tracker, folderId, cancellationToken) is { } kind)
            {
                changes.Add(await BuildAsync(db, tracker, documentId, folderId, kind, DavChangeType.Removed, uidDefinitionIds, cancellationToken));
            }
        }

        foreach (var ((documentId, folderId), _) in upserts)
        {
            if (await KindOfFolderAsync(db, tracker, folderId, cancellationToken) is { } kind)
            {
                changes.Add(await BuildAsync(db, tracker, documentId, folderId, kind, DavChangeType.Modified, uidDefinitionIds, cancellationToken));
            }
        }

        db.DavCollectionChanges.AddRange(changes);
        return changes;
    }

    private static async Task<DavCollectionChange> BuildAsync(
        SimplArchiveDbContext db, ChangeTracker tracker, Guid documentId, Guid folderId, DavCollectionKind kind,
        DavChangeType changeType, IReadOnlyCollection<Guid> uidDefinitionIds, CancellationToken cancellationToken)
    {
        var document = tracker.Entries<Document>().FirstOrDefault(e => e.Entity.Id == documentId)?.Entity
            ?? await db.Documents.IgnoreQueryFilters().AsNoTracking().FirstAsync(d => d.Id == documentId, cancellationToken);

        // The UID names the resource, exactly as the serving side derives it; the document id is the shared
        // fallback. A PENDING value in this very save wins over the stored one — it is about to be the truth.
        var pending = tracker.Entries<FieldValue>()
            .FirstOrDefault(e => e.Entity.DocumentId == documentId
                && e.State is EntityState.Added or EntityState.Modified
                && uidDefinitionIds.Contains(e.Entity.FieldDefinitionId))?.Entity.Value;
        var uid = pending ?? await db.FieldValues.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.DocumentId == documentId
                && db.FieldDefinitions.Any(f => f.Id == v.FieldDefinitionId
                    && f.Name == kind.UidFieldName
                    && db.MaskVersions.Any(mv => mv.Id == f.MaskVersionId && mv.MaskId == kind.ItemMaskId)))
            .Select(v => v.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return new DavCollectionChange
        {
            TenantId = document.TenantId,
            FolderId = folderId,
            DocumentId = documentId,
            ResourceName = (uid is { Length: > 0 } value ? value : documentId.ToString()) + kind.Extension,
            ChangeType = changeType,
            At = DateTimeOffset.UtcNow,
        };
    }

    private static async Task<Guid?> ParentOfAsync(
        SimplArchiveDbContext db, ChangeTracker tracker, Guid documentId, CancellationToken cancellationToken)
    {
        var tracked = tracker.Entries<Document>().FirstOrDefault(e => e.Entity.Id == documentId)?.Entity;
        if (tracked is not null)
        {
            return tracked.ParentId;
        }

        return await db.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => d.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<DavCollectionKind?> KindOfFolderAsync(
        SimplArchiveDbContext db, ChangeTracker tracker, Guid folderId, CancellationToken cancellationToken)
    {
        var tracked = tracker.Entries<Document>().FirstOrDefault(e => e.Entity.Id == folderId)?.Entity;
        var maskVersionId = tracked?.MaskVersionId
            ?? await db.Documents.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Id == folderId)
                .Select(d => d.MaskVersionId)
                .FirstOrDefaultAsync(cancellationToken);
        if (maskVersionId is not { } mv)
        {
            return null;
        }

        var maskId = await db.MaskVersions.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Id == mv)
            .Select(v => (Guid?)v.MaskId)
            .FirstOrDefaultAsync(cancellationToken);
        return DavCollectionKinds.ForFolderMask(maskId);
    }

    private static async Task<HashSet<Guid>> UidDefinitionIdsAsync(
        SimplArchiveDbContext db, CancellationToken cancellationToken)
    {
        var names = DavCollectionKinds.All.Select(k => k.UidFieldName).ToList();
        var maskIds = DavCollectionKinds.All.Select(k => k.ItemMaskId).ToList();
        return (await db.FieldDefinitions.IgnoreQueryFilters().AsNoTracking()
            .Where(f => names.Contains(f.Name)
                && db.MaskVersions.Any(mv => mv.Id == f.MaskVersionId && maskIds.Contains(mv.MaskId)))
            .Select(f => f.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
    }
}
