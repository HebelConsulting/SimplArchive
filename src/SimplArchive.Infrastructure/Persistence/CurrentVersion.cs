using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;

namespace SimplArchive.Infrastructure.Persistence;

// Resolves a document's **current version** honoring the explicit `Document.CurrentVersionId` pointer (ADR
// "Version-restore via a current-version pointer", issue #265): the pinned confirmed version if the pointer is
// set, else the derived latest (max `VersionNumber` among confirmed versions). Ungated — this is the
// system/background/projection sense of "current"; caller-aware workflow gating (ADR 0300) stays where it already
// lives (the version-list endpoint + content-access checks). The pointer invariant (only ever null or a confirmed
// version, kept by the restore + finalize paths, and never dangling thanks to the FK's SetNull) means a plain
// "pointer, else max-confirmed" is correct.
public static class CurrentVersion
{
    // Resolve against a caller-supplied `DocumentVersions` queryable (so the caller controls query filters, e.g. a
    // rebuilder spanning tenants via IgnoreQueryFilters). `pointerVersionId` is the document's CurrentVersionId.
    public static async Task<DocumentVersion?> ResolveAsync(
        IQueryable<DocumentVersion> versions, Guid documentId, Guid? pointerVersionId, CancellationToken cancellationToken = default)
    {
        if (pointerVersionId is Guid pinned)
        {
            var version = await versions.FirstOrDefaultAsync(
                v => v.Id == pinned && v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed,
                cancellationToken);
            if (version is not null)
            {
                return version;
            }
        }

        return await versions
            .Where(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
