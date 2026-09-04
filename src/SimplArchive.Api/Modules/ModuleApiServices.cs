using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Modules;

/// <summary>
/// The host-side implementations of the ABI's controller-facing seams (ADR 0737): thin adapters over the
/// same services core controllers use, so a module's answer can never diverge from a core one.
/// </summary>
public static class ModuleApiServices
{
    /// <summary>Registers the per-request adapters a module controller injects.</summary>
    public static IServiceCollection AddModuleApiSeams(this IServiceCollection services)
    {
        services.AddScoped<IModuleCallerContext, ModuleCallerContext>();
        services.AddScoped<IModuleDocumentRights, ModuleDocumentRights>();
        return services;
    }
}

internal sealed class ModuleCallerContext(DocumentAccessService access, ICurrentTenantAccessor tenant, SimplArchiveDbContext dbContext) : IModuleCallerContext
{
    // A module route is tenant-scoped by construction: the activation gate has already refused any request
    // with no resolvable tenant, so an empty accessor here is a programming error, not a caller state.
    public Guid TenantId => tenant.TenantId
        ?? throw new InvalidOperationException("A module endpoint was reached with no ambient tenant — the activation gate must run first.");

    public Guid? UserId => access.GetCallerIdentity().UserId;

    public Guid? ServiceAccountId => access.GetCallerIdentity().ServiceAccountId;

    public Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken = default) =>
        access.IsTenantAdminAsync(cancellationToken);

    public async Task<ModuleCallerIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        // The human-readable half of the accessors above (ABI 0.2, #1014): a user has both halves, a
        // service account a name only. The tenant query filter scopes both lookups already.
        if (UserId is { } userId)
        {
            return await dbContext.Users
                .Where(u => u.Id == userId)
                .Select(u => new ModuleCallerIdentity(u.DisplayName, u.Email))
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (ServiceAccountId is { } serviceAccountId)
        {
            return await dbContext.ServiceAccounts
                .Where(a => a.Id == serviceAccountId)
                .Select(a => new ModuleCallerIdentity(a.Name, null))
                .SingleOrDefaultAsync(cancellationToken);
        }

        return null;
    }
}

internal sealed class ModuleDocumentRights(DocumentAccessService access, SimplArchiveDbContext dbContext) : IModuleDocumentRights
{
    public async Task<ModuleDocumentRightsAnswer> GetAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        // The ABI promises all-false for a document that does not exist (in this tenant). The core
        // calculator alone cannot keep that promise: the tenant-admin bypass short-circuits to full rights
        // BEFORE the document is resolved, so an admin asking about a ghost id would read all-true — and a
        // module reasoning from rights on a ghost is exactly the trap the promise exists to close.
        if (!await dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return new ModuleDocumentRightsAnswer(false, false, false, false, false, false, false, false, false);
        }

        var r = await access.GetCallerRightsAsync(documentId, cancellationToken);
        return new ModuleDocumentRightsAnswer(
            r.CanSee, r.CanReadContent, r.CanEditContent, r.CanEditIndexData,
            r.CanCreateSubItems, r.CanDelete, r.CanManagePermissions, r.CanMove, r.CanAnnotate);
    }
}
