using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// The per-module service principal (ADR 0736): a login-less ServiceAccount the activation creates, and the
// facade's consent gate that reads through ITS grants — an ungranted module honestly sees NOTHING, a
// granted one exactly what the administrator consented to, and core-internal use (no module identity)
// stays ungated.
public class ModulePrincipalTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;
        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private sealed class TestUserAccessor : SimplArchive.Application.Abstractions.ICurrentUserAccessor
    {
        public Guid? UserId { get; set; }
    }

    private sealed class TestServiceAccountAccessor : SimplArchive.Application.Abstractions.ICurrentServiceAccountAccessor
    {
        public Guid? ServiceAccountId { get; set; }
    }

    private sealed record Rig(SimplArchiveDbContext Context, Guid TenantId, Guid UserId, Guid RootId, Guid DocumentId);

    private static async Task<Rig> RigAsync(SqliteConnection connection)
    {
        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var context = CreateContext(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "p@example.com", DisplayName = "P", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        // The module's mask + a document wearing it, under the root the grants will land on.
        var maskVersionId = Guid.NewGuid();
        context.Masks.Add(new Mask { Id = TestModule.TestModule.DossierMaskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        context.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = TestModule.TestModule.DossierMaskId, Name = "Test Dossier", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = documentId, TenantId = tenantId, ParentId = rootId, Name = "Dossier", MaskVersionId = maskVersionId, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        return new Rig(context, tenantId, userId, rootId, documentId);
    }

    [Fact]
    public async Task Ensure_creates_the_login_less_principal_once()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        var first = await ModulePrincipal.EnsureAsync(rig.Context, "test-module", "Test Module", rig.TenantId, CancellationToken.None);
        var second = await ModulePrincipal.EnsureAsync(rig.Context, "test-module", "Test Module", rig.TenantId, CancellationToken.None);

        Assert.Equal(first.Id, second.Id); // the activation's upsert, not a duplicate per renewal
        Assert.Equal("module:test-module", first.OpenIddictApplicationClientId);
        Assert.Equal("Module: Test Module", first.Name);
        Assert.Single(rig.Context.ServiceAccounts.Local);
    }

    [Fact]
    public async Task Facade_reads_are_gated_by_the_principals_grants_and_ungated_for_core_use()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var principal = await ModulePrincipal.EnsureAsync(rig.Context, "test-module", "Test Module", rig.TenantId, CancellationToken.None);

        var identity = new ModuleIdentityAccessor { ModuleId = "test-module" };
        var rights = new EffectiveRightsCalculator(rig.Context);
        var moduleEyes = new ModuleArchiveFacade(rig.Context, new TestUserAccessor { UserId = rig.UserId }, new TestServiceAccountAccessor(), identity, rights);

        // Ungranted: the module sees nothing — not the document, not the mask listing.
        Assert.Null(await moduleEyes.GetDocumentAsync(rig.DocumentId));
        Assert.Empty(await moduleEyes.GetByMaskAsync(TestModule.TestModule.DossierMaskId));

        // The consent act: an ordinary ACL grant to the module's own principal, on the root.
        rig.Context.AclEntries.Add(new AclEntry
        {
            Id = Guid.NewGuid(),
            TenantId = rig.TenantId,
            DocumentId = rig.RootId,
            ServiceAccountId = principal.Id,
            CanSee = true,
        });
        await rig.Context.SaveChangesAsync();

        Assert.NotNull(await moduleEyes.GetDocumentAsync(rig.DocumentId));
        Assert.Single(await moduleEyes.GetByMaskAsync(TestModule.TestModule.DossierMaskId));

        // Core-internal use (no module identity) stays ungated — the core is not a tenant of its own
        // consent machinery.
        var coreEyes = new ModuleArchiveFacade(rig.Context, new TestUserAccessor { UserId = rig.UserId }, new TestServiceAccountAccessor());
        Assert.NotNull(await coreEyes.GetDocumentAsync(rig.DocumentId));
    }
}
