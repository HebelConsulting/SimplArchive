using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Manuals;

/// <summary>
/// The tenant's "SimplArchive Manuals" folder (ADR 0891): found by its MASK, created lazily directly under the
/// standard repository (ADR 0892), and moved with it when the standard repository changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by mask, never by name.</b> An administrator may rename the folder or move it; it is still the one
/// wearing "SimplArchive Documentation", and SaveChanges guarantees there is at most one live such folder per tenant
/// and that only the core puts the mask on it.
/// </para>
/// <para>
/// <b>A name a user already took is not ours to take.</b> If the standard repository already holds a document
/// called "SimplArchive Manuals" that is not the manuals folder, the folder is created as "SimplArchive Manuals (2)"
/// (then 3, …) and a Warning names both — adopting the user's folder by its name would type somebody's own content as
/// core-owned, and refusing would leave the tenant without manuals over a coincidence of naming.
/// </para>
/// </remarks>
public static class ManualsFolder
{
    public const string Name = "SimplArchive Manuals";

    /// <summary>The live folder wearing the Documentation mask in the current tenant, or null.</summary>
    public static Task<Document?> FindAsync(SimplArchiveDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.Documentation))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The manuals folder, created under the standard repository when there is none; null (and a Warning) when the
    /// tenant has no standard repository to create it in.
    /// </summary>
    public static async Task<Document?> FindOrCreateAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, ILogger logger, CancellationToken cancellationToken)
    {
        if (await FindAsync(dbContext, cancellationToken) is { } existing)
        {
            return existing;
        }

        var standardId = await dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.StandardRepositoryId)
            .SingleOrDefaultAsync(cancellationToken);
        var standard = standardId is { } id
            ? await dbContext.Documents.SingleOrDefaultAsync(d => d.Id == id, cancellationToken)
            : null;
        if (standard is null)
        {
            logger.LogWarning(
                "Tenant {TenantId} has no standard repository, so no manual was filed. A tenant administrator can choose "
                + "one in the tenant settings; the manuals are filed there at the next start.", tenantId);
            return null;
        }

        var maskVersionId = await dbContext.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Documentation && v.IsCurrent)
            .Select(v => (Guid?)v.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (maskVersionId is null)
        {
            logger.LogWarning("Tenant {TenantId} has no '{Mask}' mask yet, so no manual was filed; the next start heals it.",
                tenantId, "SimplArchive Documentation");
            return null;
        }

        var name = await FreeNameAsync(dbContext, standard.Id, cancellationToken);
        if (!string.Equals(name, Name, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Tenant {TenantId}: the standard repository already holds a document named '{Taken}' that is not the "
                + "manuals folder, so the manuals folder was created as '{Name}'. Rename either as you prefer — the "
                + "manuals folder is recognised by its type, not its name.", tenantId, Name, name);
        }

        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = standard.Id,
            Name = name,
            MaskVersionId = maskVersionId.Value,
            // The standard repository's own author: the core files on the tenant's behalf, and a document needs a
            // principal (the exactly-one-creator CHECK). Provisioning made that repository, so this is its administrator.
            CreatedByUserId = standard.CreatedByUserId,
            CreatedByServiceAccountId = standard.CreatedByUserId is null ? standard.CreatedByServiceAccountId : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PermitCoreOwnedMask(folder.Id);
        dbContext.Documents.Add(folder);
        await dbContext.SaveChangesAsync(cancellationToken);
        return folder;
    }

    internal static async Task<string> FreeNameAsync(
        SimplArchiveDbContext dbContext, Guid parentId, CancellationToken cancellationToken, string keep = Name)
    {
        var taken = await dbContext.Documents
            .Where(d => d.ParentId == parentId)
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);
        if (!taken.Contains(keep, StringComparer.OrdinalIgnoreCase))
        {
            return keep;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{keep} ({n})";
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }
}
