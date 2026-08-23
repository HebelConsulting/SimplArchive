using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Provisioning;

// A machine principal seeded from configuration for a seeded tenant (ADR 0585), used by both the demo tenant
// (the migration SOURCE) and the interop tenant (the TARGET) — one implementation taking the config section,
// because the two differ in nothing but which section they read.
//
// Why the credentials come from config rather than being generated: a service account's secret is shown ONCE at
// creation and stored hashed, so anything outside the system that has to authenticate must be told it in
// advance. `bootstrap-platform-admin` already works exactly this way. Before this, every `docker compose down -v`
// invalidated the migration tooling's saved credentials and the next run died at `invalid_client` — an error
// that says nothing about volumes.
public static class SeededServiceAccount
{
    // Adds the account + its OpenIddict application + a full ACL entry on the tenant's repository root. A no-op
    // unless <sectionPrefix>:ServiceAccount:ClientId/ClientSecret are both configured. Does NOT save — the caller
    // owns the transaction boundary, since it is mid-way through seeding a tenant.
    public static async Task<bool> AddIfConfiguredAsync(
        IServiceProvider services, SimplArchiveDbContext dbContext, IConfiguration configuration,
        string sectionPrefix, ProvisionedTenant provisioned)
    {
        var clientId = configuration[$"{sectionPrefix}:ServiceAccount:ClientId"];
        var clientSecret = configuration[$"{sectionPrefix}:ServiceAccount:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return false;
        }

        var applicationManager = services.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applicationManager.FindByClientIdAsync(clientId) is null)
        {
            await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
                },
            });
        }

        var serviceAccount = new ServiceAccount
        {
            Id = Guid.NewGuid(),
            TenantId = provisioned.TenantId,
            Name = configuration[$"{sectionPrefix}:ServiceAccount:Name"] ?? sectionPrefix.ToLowerInvariant(),
            OpenIddictApplicationClientId = clientId,
            IsActive = true,
            CanManageRepositories = true,
            CanManageMasks = true,
            CanImport = true,
            CanExport = true,
            // A migration writes departmental mailboxes WITH their address claims (#703), so the seeded
            // interop principal holds the routing right the way it holds the others it works with.
            CanManageMailRouting = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.ServiceAccounts.Add(serviceAccount);

        // The grant is the half that is easy to forget and impossible to diagnose from the error: repository
        // listing is per-item ACL filtered, so an account holding only system rights sees an EMPTY archive and a
        // migration fails with "repository not found" — which reads as a wrong name rather than as missing
        // permissions. CanManagePermissions is additionally what lets an export carry ACLs at all (ADR 0539).
        dbContext.AclEntries.Add(new AclEntry
        {
            Id = Guid.NewGuid(),
            TenantId = provisioned.TenantId,
            DocumentId = provisioned.RepositoryId,
            ServiceAccountId = serviceAccount.Id,
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanEditIndexData = true,
            CanCreateSubItems = true,
            CanDelete = true,
            CanMove = true,
            CanAnnotate = true,
            CanManagePermissions = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        return true;
    }
}
