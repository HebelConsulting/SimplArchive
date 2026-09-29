using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// A document whose mask takes exactly one version refuses a second (ADR 0848,
/// <see cref="WellKnownMaskIds.SingleVersionMasks"/>) — <c>Certificate</c> today.
/// </summary>
/// <remarks>
/// <para>
/// Called from <c>SaveChanges</c> rather than from an upload endpoint, because that is the sole enforcement
/// point: versions are added by the finalizer, by IMAP, by WebDAV, by CalDAV's replace and by the module
/// engine, and a check per entrance is how the audit trail came to disagree with itself. Its own class rather
/// than a method on the context for the 1000-line rule — the context was one change away from the limit.
/// </para>
/// </remarks>
internal static class SingleVersionMaskRule
{
    internal static async Task ValidateAsync(SimplArchiveDbContext db, CancellationToken cancellationToken)
    {
        var added = db.ChangeTracker.Entries<DocumentVersion>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        if (added.Count == 0)
        {
            return;
        }

        foreach (var version in added)
        {
            // The document may be tracked (the ordinary upload) or not (a path that only adds the version),
            // so the tracker is consulted first and the database only for what it does not hold.
            var document = db.ChangeTracker.Entries<Document>()
                .Select(e => e.Entity)
                .FirstOrDefault(d => d.Id == version.DocumentId)
                ?? await db.Documents
                    .IgnoreQueryFilters(["SoftDeleteFilter"])
                    .FirstOrDefaultAsync(d => d.Id == version.DocumentId, cancellationToken);

            if (document?.MaskVersionId is not { } maskVersionId || maskVersionId == Guid.Empty)
            {
                continue;
            }

            var maskId = await db.MaskVersions
                .Where(v => v.Id == maskVersionId)
                .Select(v => (Guid?)v.MaskId)
                .FirstOrDefaultAsync(cancellationToken);

            if (maskId is not { } mask || !WellKnownMaskIds.SingleVersionMasks.Contains(mask))
            {
                continue;
            }

            // The FIRST version is what makes the document usable, so the rule is "no SECOND", not "none".
            // Counting the database rather than the tracker: the first version is normally added in the same
            // save as the document, and both would otherwise be refused together.
            var existing = await db.DocumentVersions
                .CountAsync(v => v.DocumentId == version.DocumentId && v.Id != version.Id, cancellationToken);

            if (existing > 0)
            {
                throw new SingleVersionMaskException(
                    $"'{document.Name}' wears a mask whose documents take exactly one version, so a second "
                    + "cannot be filed against it. A replacement is a new document: file it as its own "
                    + "enrolment and revoke this one, which stays as the record that it once applied.");
            }
        }
    }
}
