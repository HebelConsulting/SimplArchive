using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The list-field half of the facade (ABI 0.2, #1014): SetFieldListAsync is a replace-write of one field's
// ordered rows, and FieldLists is the faithful read the "+"-joined Fields never was. Found building the
// first real module — the flight-log entry's counter readings are three aligned list fields by decided
// design (flight-school ADR 0004), and ABI 0.1 could not write a list at all.
public class ModuleFacadeFieldListTests
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

    private sealed record Rig(ModuleArchiveFacade Facade, Guid RootId, Guid EntryId);

    private static async Task<Rig> RigAsync(SqliteConnection connection)
    {
        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "l@example.com", DisplayName = "L", CreatedAt = DateTimeOffset.UtcNow });
            seed.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        var module = new TestModule.TestModule();
        using (var seedContext = CreateContext(connection, tenantId))
        {
            await new ModuleMaskSeeder(seedContext, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, tenantId);
        }

        var context = CreateContext(connection, tenantId);
        var facade = new ModuleArchiveFacade(context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor());
        var entryId = await facade.CreateDocumentAsync(rootId, TestModule.TestModule.EntryMaskId, "Entry");
        return new Rig(facade, rootId, entryId);
    }

    [Fact]
    public async Task A_list_field_round_trips_in_order_and_replaces_on_rewrite()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        await rig.Facade.SetFieldListAsync(rig.EntryId, "Tags", ["FTC 1", "Hobbs", "Tacho"]);
        var document = await rig.Facade.GetDocumentAsync(rig.EntryId);
        Assert.NotNull(document);
        Assert.Equal(["FTC 1", "Hobbs", "Tacho"], document.FieldLists["Tags"]);
        Assert.Equal("FTC 1+Hobbs+Tacho", document.Fields["Tags"]); // the 0.1 joined form, unchanged

        // Replace-write: what you pass is what the field holds afterwards — order included.
        await rig.Facade.SetFieldListAsync(rig.EntryId, "Tags", ["Tacho", "FTC 1"]);
        document = await rig.Facade.GetDocumentAsync(rig.EntryId);
        Assert.Equal(["Tacho", "FTC 1"], document!.FieldLists["Tags"]);
    }

    [Fact]
    public async Task An_empty_list_clears_the_field()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await rig.Facade.SetFieldListAsync(rig.EntryId, "Tags", ["FTC 1"]);

        await rig.Facade.SetFieldListAsync(rig.EntryId, "Tags", []);

        var document = await rig.Facade.GetDocumentAsync(rig.EntryId);
        Assert.False(document!.FieldLists.ContainsKey("Tags"));
        Assert.False(document.Fields.ContainsKey("Tags"));
    }

    [Fact]
    public async Task A_field_the_mask_does_not_define_is_refused()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        await Assert.ThrowsAsync<ArgumentException>(
            () => rig.Facade.SetFieldListAsync(rig.EntryId, "No such field", ["x"]));
    }

    [Fact]
    public async Task Rename_replaces_the_name_and_the_sibling_invariant_still_applies()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        await rig.Facade.RenameDocumentAsync(rig.EntryId, "2026-09-04 LSPG 1030 LSPG 1125 P28A HBPHG");
        Assert.Equal("2026-09-04 LSPG 1030 LSPG 1125 P28A HBPHG", (await rig.Facade.GetDocumentAsync(rig.EntryId))!.Name);

        // A second entry cannot take the same name: the module's rename goes through SaveChanges and its
        // sibling-name invariant like anyone else's (ABI 0.2, #1014).
        var secondId = await rig.Facade.CreateDocumentAsync(rig.RootId, TestModule.TestModule.EntryMaskId, "Second");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Facade.RenameDocumentAsync(secondId, "2026-09-04 LSPG 1030 LSPG 1125 P28A HBPHG"));
    }

    [Fact]
    public async Task A_single_valued_field_reads_as_a_one_element_list()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var certificateId = await rig.Facade.CreateDocumentAsync(rig.RootId, TestModule.TestModule.CertificateMaskId, "Medical",
            new Dictionary<string, string> { ["Valid to"] = "2027-01-01" });

        var document = await rig.Facade.GetDocumentAsync(certificateId);

        Assert.Equal(["2027-01-01"], document!.FieldLists["Valid to"]);
        Assert.Equal("2027-01-01", document.Fields["Valid to"]);
    }
}
