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

    // Same lookup for a caller that runs BEFORE the current tenant is set — tenant provisioning, which a
    // PlatformAdministrator drives with no tenant of its own. The tenant filter would match zero rows there
    // (the standing rule for pre-tenant lookups, e.g. TokenController), so it is named-ignored and the tenant
    // supplied explicitly — otherwise a tenant's first repository silently comes out with no mask at all.
    public static Task<Guid?> CurrentVersionIdAsync(SimplArchiveDbContext dbContext, Guid tenantId, CancellationToken cancellationToken) =>
        CurrentVersionIdAsync(dbContext, tenantId, WellKnownMaskIds.Folder, cancellationToken);

    /// <summary>Any well-known mask's current version for a given tenant — the personal-space mask included
    /// (ADR 0590). Returns null when that mask is not seeded, so a caller can fall back rather than fail.</summary>
    public static async Task<Guid?> CurrentVersionIdAsync(SimplArchiveDbContext dbContext, Guid tenantId, Guid maskId, CancellationToken cancellationToken) =>
        await dbContext.MaskVersions
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(mv => mv.TenantId == tenantId && mv.MaskId == maskId && mv.IsCurrent)
            .Select(mv => (Guid?)mv.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // Whether a mask-version id belongs to the Folder well-known mask (so finalize treats it as unclassified).
    public static async Task<bool> IsFolderMaskAsync(SimplArchiveDbContext dbContext, Guid? maskVersionId, CancellationToken cancellationToken) =>
        maskVersionId is { } id && await dbContext.MaskVersions.AnyAsync(mv => mv.Id == id && mv.MaskId == WellKnownMaskIds.Folder, cancellationToken);
}
