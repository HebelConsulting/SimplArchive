using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// ADR "Folder mask on folders" assigns the Folder mask on every folder-creation path — including a tenant's
// first repository, created by TenantProvisioningService. That one runs BEFORE the current tenant is set (a
// PlatformAdministrator has no tenant of its own), so the tenant query filter matched zero MaskVersions and
// the lookup quietly returned null: every provisioned tenant's repository root came out with no mask at all,
// silently violating the ADR. Found by the external-DMS interop export, whose target system rejects a folder
// that carries no folder-capable mask. The tenant-explicit overload is the fix; these tests pin both halves.
public class FolderMaskTenantScopeTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<Guid> SeedTenantWithWellKnownMasksAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();

        using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        // The same seeder tenant provisioning uses, so the masks are seeded exactly as they are in production.
        await new WellKnownMaskSeeder(context).EnsureWellKnownMasksAsync(tenantId);

        return tenantId;
    }

    [Fact]
    public async Task Resolves_the_folder_mask_for_a_caller_that_has_no_current_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantId = await SeedTenantWithWellKnownMasksAsync(connection);

        using var provisioning = CreateContext(connection); // no current tenant — provisioning's situation
        Assert.NotNull(await FolderMask.CurrentVersionIdAsync(provisioning, tenantId, CancellationToken.None));
    }

    [Fact]
    public async Task Tenant_scoped_lookup_finds_nothing_without_a_current_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedTenantWithWellKnownMasksAsync(connection);

        // The regression itself: no current tenant → the filter matches nothing → a maskless folder. This is
        // why the provisioning path must pass the tenant explicitly rather than rely on the ambient one.
        using var provisioning = CreateContext(connection);
        Assert.Null(await FolderMask.CurrentVersionIdAsync(provisioning, CancellationToken.None));
    }

    [Fact]
    public async Task Both_overloads_agree_once_the_tenant_is_current()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantId = await SeedTenantWithWellKnownMasksAsync(connection);

        using var context = CreateContext(connection, tenantId);
        var ambient = await FolderMask.CurrentVersionIdAsync(context, CancellationToken.None);
        var explicitTenant = await FolderMask.CurrentVersionIdAsync(context, tenantId, CancellationToken.None);

        Assert.NotNull(ambient);
        Assert.Equal(ambient, explicitTenant);
    }
}
