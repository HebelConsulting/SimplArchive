using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// WHICH module's code is running in this scope (ADR 0736): set by the host at the module boundaries —
/// the controller gate, the engine, the rebuild endpoint — and read by the facade to gate its reads under
/// the module's own principal. Null means core-internal use (license stamping and the like), which stays
/// ungated: the core is not a tenant of its own consent machinery.
/// </summary>
public sealed class ModuleIdentityAccessor
{
    public string? ModuleId { get; set; }
}

/// <summary>
/// The per-module service principal (ADR 0736): a real <see cref="ServiceAccount"/> per tenant, created
/// at activation — so it appears in the grant pickers, the admin list and the audit trail like any other
/// principal, and the licensing act doubles as the consent act. Its client id has NO OpenIddict
/// application behind it, so a wire login is impossible by construction; the identity exists only for
/// rights and attribution.
/// </summary>
public static class ModulePrincipal
{
    /// <summary>The synthetic client id — the stable per-tenant lookup key, never a login.</summary>
    public static string ClientIdFor(string moduleId) => $"module:{moduleId}";

    /// <summary>The module's principal in the ambient tenant, or null when it was never activated.</summary>
    public static Task<ServiceAccount?> FindAsync(SimplArchiveDbContext dbContext, string moduleId, CancellationToken cancellationToken) =>
        dbContext.ServiceAccounts.SingleOrDefaultAsync(
            s => s.OpenIddictApplicationClientId == ClientIdFor(moduleId), cancellationToken);

    /// <summary>Creates the principal if this tenant does not hold one yet — the activation's upsert.</summary>
    public static async Task<ServiceAccount> EnsureAsync(
        SimplArchiveDbContext dbContext, string moduleId, string displayName, Guid tenantId, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(dbContext, moduleId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var principal = new ServiceAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = $"Module: {displayName}",
            OpenIddictApplicationClientId = ClientIdFor(moduleId),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.ServiceAccounts.Add(principal);
        await dbContext.SaveChangesAsync(cancellationToken);
        return principal;
    }
}
