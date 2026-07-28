using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

// Helpers for the "Folder" well-known mask (ADR "Folder mask on folders"). Every folder is assigned the
// tenant's current Folder mask version at creation so it reads as type "Folder" rather than
// "(no mask)". A leaf that later gets a version is reclassified away from Folder at finalize (see
// DocumentFinalizer), so the Folder mask is treated as "still unclassified" there.
public static class FolderMask
{
    // The tenant's current Folder mask version (null only if the well-known masks aren't seeded — shouldn't
    // happen for a provisioned tenant). Tenant-scoped via the DbContext query filter.
    public static async Task<Guid?> CurrentVersionIdAsync(SimplArchiveDbContext dbContext, CancellationToken cancellationToken) =>
        await dbContext.MaskVersions
            .Where(mv => mv.MaskId == WellKnownMaskIds.Folder && mv.IsCurrent)
            .Select(mv => (Guid?)mv.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // Whether a mask-version id belongs to the Folder well-known mask (so finalize treats it as unclassified).
    public static async Task<bool> IsFolderMaskAsync(SimplArchiveDbContext dbContext, Guid? maskVersionId, CancellationToken cancellationToken) =>
        maskVersionId is { } id && await dbContext.MaskVersions.AnyAsync(mv => mv.Id == id && mv.MaskId == WellKnownMaskIds.Folder, cancellationToken);
}
