using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.TestModule;

namespace SimplArchive.IntegrationTests;

// The module seam's host side (ADR 0741), against the real DbContext: the seeder plants and heals a
// module's masks the way the core's own well-known seeder does, and the facade's five operations run under
// the core's invariants — a module's writes are nobody special. The fixture is the same real TestModule
// the loader tests use, so the whole seam is exercised with an assembly that sees only the ABI.
public class ModuleSeamTests
{
    private sealed class TestUserAccessor : ICurrentUserAccessor
    {
        public Guid? UserId { get; set; }
    }

    private sealed class TestServiceAccountAccessor : ICurrentServiceAccountAccessor
    {
        public Guid? ServiceAccountId { get; set; }
    }

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;
        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private static async Task<(Guid TenantId, Guid UserId, Guid RootId)> SeedTenantAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "m@example.com", DisplayName = "M", CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();
        return (tenantId, userId, rootId);
    }

    [Fact]
    public async Task The_seeder_plants_a_modules_mask_and_is_idempotent()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var (tenantId, _, _) = await SeedTenantAsync(connection);

        var module = new TestModule.TestModule();
        using (var context = CreateContext(connection, tenantId))
        {
            var seeder = new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance);
            await seeder.SeedAsync(module, tenantId);
            await seeder.SeedAsync(module, tenantId); // idempotent — activation may run again on upgrade
        }

        using var check = CreateContext(connection, tenantId);
        var mask = await check.Masks.SingleAsync(m => m.Id == TestModule.TestModule.CertificateMaskId);
        Assert.False(mask.IsBookable);
        var version = await check.MaskVersions.SingleAsync(v => v.MaskId == mask.Id && v.IsCurrent);
        Assert.Equal("Test Certificate", version.Name);
        // Two since the fixture's certificate gained the temporarily-void flag (the engine's medical shape).
        Assert.Equal(2, await check.FieldDefinitions.CountAsync(f => f.MaskVersionId == version.Id));
    }

    [Fact]
    public async Task The_facade_creates_reads_writes_and_references_under_the_calling_identity()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var (tenantId, userId, rootId) = await SeedTenantAsync(connection);

        var module = new TestModule.TestModule();
        using (var seedContext = CreateContext(connection, tenantId))
        {
            await new ModuleMaskSeeder(seedContext, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, tenantId);
        }

        using var context = CreateContext(connection, tenantId);
        var facade = new ModuleArchiveFacade(context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor());

        var certId = await facade.CreateDocumentAsync(rootId, TestModule.TestModule.CertificateMaskId, "Medical 2026",
            new Dictionary<string, string> { ["Valid to"] = "2026-12-31" });

        var read = await facade.GetDocumentAsync(certId);
        Assert.NotNull(read);
        Assert.Equal(TestModule.TestModule.CertificateMaskId, read.MaskId);
        Assert.Equal("2026-12-31", read.Fields["Valid to"]);

        await facade.SetFieldsAsync(certId, new Dictionary<string, string> { ["Valid to"] = "2027-06-30" });
        Assert.Equal("2027-06-30", (await facade.GetDocumentAsync(certId))!.Fields["Valid to"]);

        var children = await facade.GetChildrenAsync(rootId, TestModule.TestModule.CertificateMaskId);
        Assert.Equal(certId, Assert.Single(children).Id);

        // One entry, referenced — never copied (module ADR 0002's shape, the facade's job to make cheap).
        var logbookId = Guid.NewGuid();
        context.Documents.Add(new Document { Id = logbookId, TenantId = tenantId, Name = "Logbook", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        await facade.CreateReferenceAsync(certId, logbookId);
        Assert.Single(await context.DocumentReferences.Where(r => r.TargetDocumentId == certId && r.ParentFolderId == logbookId).ToListAsync());
    }

    [Fact]
    public async Task An_unknown_field_name_is_refused_by_name_never_guessed()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var (tenantId, userId, rootId) = await SeedTenantAsync(connection);
        using (var seedContext = CreateContext(connection, tenantId))
        {
            await new ModuleMaskSeeder(seedContext, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new TestModule.TestModule(), tenantId);
        }

        using var context = CreateContext(connection, tenantId);
        var facade = new ModuleArchiveFacade(context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor());
        var certId = await facade.CreateDocumentAsync(rootId, TestModule.TestModule.CertificateMaskId, "Medical");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            facade.SetFieldsAsync(certId, new Dictionary<string, string> { ["No Such Field"] = "x" }));
    }

    [Fact]
    public async Task The_facade_refuses_to_invent_an_identity()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var (tenantId, _, rootId) = await SeedTenantAsync(connection);
        using (var seedContext = CreateContext(connection, tenantId))
        {
            await new ModuleMaskSeeder(seedContext, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new TestModule.TestModule(), tenantId);
        }

        using var context = CreateContext(connection, tenantId);
        var facade = new ModuleArchiveFacade(context, new TestUserAccessor(), new TestServiceAccountAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.CreateDocumentAsync(rootId, TestModule.TestModule.CertificateMaskId, "Anonymous"));
    }
}
