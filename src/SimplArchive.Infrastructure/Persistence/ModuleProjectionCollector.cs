using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Which documents a save changed, for the modules that project them (ABI 1.1, ADR 0848).
/// </summary>
/// <remarks>
/// <para>
/// Same two-step shape as <c>RealtimeChangePusher</c> and <c>DavChangeRecorder</c>: COLLECT from the
/// <c>ChangeTracker</c> before <c>base.SaveChangesAsync</c>, DISPATCH after it — because the entity states are
/// gone once the save returns, and because the module must read COMMITTED values rather than pending ones a
/// rollback would discard.
/// </para>
/// <para>
/// <b>The predicate is the difficult part, and the obvious version of it is wrong.</b> "A <c>Document</c>
/// Added or Modified" misses the two ways a certificate is actually revoked or replaced — see
/// <see cref="CollectAsync"/>.
/// </para>
/// </remarks>
internal static class ModuleProjectionCollector
{
    /// <summary>One document to re-project, after the save commits.</summary>
    internal readonly record struct Change(Guid DocumentId, Guid MaskId, Guid TenantId, bool Removed);

    /// <summary>
    /// Everything this save changed that some module projects, one entry per DOCUMENT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THREE entities can mean "a projected document changed", and only the first is obvious:
    /// </para>
    /// <list type="bullet">
    /// <item><b><see cref="Document"/></b> — added, modified, or soft-deleted.</item>
    /// <item><b><see cref="FieldValue"/></b> — index fields are EAV, so setting <c>Revoked on</c> writes one of
    /// THESE and leaves the document row untouched. A predicate over documents alone sails straight past the
    /// revocation ADR 0848 exists to catch.</item>
    /// <item><b><see cref="DocumentVersion"/></b> — filing version 2 adds one of these and, again, does not
    /// touch the document row. Only a CONFIRMED version counts: a version is written Pending and finalized
    /// later, and projecting the pending one would publish a certificate that may never be confirmed.</item>
    /// </list>
    /// <para>
    /// A move of <see cref="Document.CurrentVersionId"/> is covered by the document branch, and it matters
    /// because the current version is DERIVED (latest confirmed unless pinned, ADR 0503) — so a pin change is
    /// a content change with no new version at all.
    /// </para>
    /// </remarks>
    internal static async Task<List<Change>> CollectAsync(
        SimplArchiveDbContext db, IReadOnlyCollection<Guid> projectedMasks, CancellationToken cancellationToken)
    {
        // The cheap gate FIRST: no module projects anything in most deployments, and this must then cost one
        // count check rather than three ChangeTracker walks on every save in the system.
        if (projectedMasks.Count == 0)
        {
            return [];
        }

        var tracker = db.ChangeTracker;
        var candidates = new HashSet<Guid>();
        var documents = new Dictionary<Guid, Document>();

        foreach (var entry in tracker.Entries<Document>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                candidates.Add(entry.Entity.Id);
                documents[entry.Entity.Id] = entry.Entity;
            }
        }

        foreach (var entry in tracker.Entries<FieldValue>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                candidates.Add(DocumentIdOf(entry));
            }
        }

        foreach (var entry in tracker.Entries<DocumentVersion>())
        {
            // Confirmed only, and "became confirmed" rather than "is confirmed": a modification that leaves an
            // already-confirmed version alone (a page count, a hash) is not a new certificate.
            var confirmed = entry.Entity.Status == DocumentVersionStatus.Confirmed;
            var wasConfirmed = entry.State == EntityState.Modified
                && entry.Property(v => v.Status).OriginalValue == DocumentVersionStatus.Confirmed;

            if (confirmed && !wasConfirmed && entry.State is EntityState.Added or EntityState.Modified)
            {
                candidates.Add(entry.Entity.DocumentId);
            }
        }

        candidates.Remove(Guid.Empty);
        if (candidates.Count == 0)
        {
            return [];
        }

        // The mask lives on the DOCUMENT, and a field value or a version gives no route to it — so the ones
        // the tracker did not already hand us are a query. It runs only when a declared mask exists at all,
        // which the gate above has already established.
        var unknown = candidates.Where(id => !documents.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            // IgnoreQueryFilters on the SOFT DELETE only: a document soft-deleted in this very save must still
            // resolve, or a deletion would silently fail to reach the projection — the dangerous direction.
            // The tenant filter stays on, so this can never reach across tenants.
            var fetched = await db.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => unknown.Contains(d.Id))
                .ToListAsync(cancellationToken);

            foreach (var document in fetched)
            {
                documents[document.Id] = document;
            }
        }

        // A document wears a MASK VERSION, never a mask (ADR 0166/0198) — the mask id is one hop further on,
        // and the tracker cannot supply it because a new version of the mask is its own row. One query for
        // every candidate rather than one each: a save that touches twenty certificates is still one round
        // trip, and this runs inside the core's save where a per-row query would be felt.
        var maskVersionIds = documents.Values
            .Select(d => d.MaskVersionId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var maskByVersion = await db.MaskVersions
            .Where(v => maskVersionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.MaskId })
            .ToDictionaryAsync(v => v.Id, v => v.MaskId, cancellationToken);

        var changes = new List<Change>();
        foreach (var id in candidates)
        {
            if (!documents.TryGetValue(id, out var document)
                || !maskByVersion.TryGetValue(document.MaskVersionId, out var maskId)
                || !projectedMasks.Contains(maskId))
            {
                continue;
            }

            // Removed covers both ways a reader stops being addressed: the document went to the recycle bin,
            // or somebody changed its mask to one nobody projects. The second reaches here only via the
            // document branch, which is why the mask is resolved from the ENTITY's current mask version.
            changes.Add(new Change(id, maskId, document.TenantId, Removed: document.DeletedAt is not null));
        }

        return changes;
    }

    private static Guid DocumentIdOf(EntityEntry<FieldValue> entry) =>
        // A Deleted entry's current values are unreliable, so the original is the honest read; for the other
        // states they agree. Same shape as the ChangeTracker rule elsewhere: read the state you mean.
        entry.State == EntityState.Deleted
            ? entry.Property(f => f.DocumentId).OriginalValue
            : entry.Entity.DocumentId;
}
