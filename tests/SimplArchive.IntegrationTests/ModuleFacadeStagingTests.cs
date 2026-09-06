using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Content staging (ABI 0.6): StageContentAsync writes EPHEMERAL bytes under fs/special/ with an expiry the
// sweep honours; CreateContentDocumentAsync writes PERMANENT reference data under the archive keyspace; both
// share one WriteContentAsync. The DABS/METAR-as-ephemeral and the ICAO-list-as-a-document thread.
public class ModuleFacadeStagingTests
{
    private sealed class TestUserAccessor : ICurrentUserAccessor { public Guid? UserId { get; set; } }
    private sealed class TestServiceAccountAccessor : ICurrentServiceAccountAccessor { public Guid? ServiceAccountId { get; set; } }

    private static SimplArchiveDbContext Ctx(SqliteConnection c, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Rig(ModuleArchiveFacade Facade, InMemoryObjectStorage Storage, Guid TenantId, Guid RootId);

    private static async Task<Rig> RigAsync(SqliteConnection connection)
    {
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        using (var seed = Ctx(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "l@x.io", DisplayName = "L", CreatedAt = DateTimeOffset.UtcNow });
            seed.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using (var seedContext = Ctx(connection, tenantId))
        {
            await new ModuleMaskSeeder(seedContext, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new TestModule.TestModule(), tenantId);
        }

        var storage = new InMemoryObjectStorage();
        var facade = new ModuleArchiveFacade(
            Ctx(connection, tenantId), new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor(), objectStorage: storage);
        return new Rig(facade, storage, tenantId, rootId);
    }

    [Fact]
    public async Task StageContentAsync_writes_an_ephemeral_doc_under_fs_special_with_an_expiry()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 DABS");
        var expiry = DateTimeOffset.UtcNow.AddHours(20);

        var id = await rig.Facade.StageContentAsync(rig.RootId, TestModule.TestModule.EntryMaskId, "DABS today", bytes, "pdf", expiry);

        using var check = Ctx(connection, rig.TenantId);
        var doc = await check.Documents.SingleAsync(d => d.Id == id);
        Assert.Equal(expiry, doc.ExpiresAt);
        var version = await check.DocumentVersions.SingleAsync(v => v.DocumentId == id);
        Assert.Equal(DocumentVersionStatus.Confirmed, version.Status);
        Assert.Contains("/fs/special/", version.ObjectKey);
        Assert.True(rig.Storage.Objects.ContainsKey(version.ObjectKey));
        Assert.Equal(bytes, rig.Storage.Objects[version.ObjectKey]);
        Assert.Equal(bytes, await rig.Facade.GetDocumentContentAsync(id)); // readable back through the content API
    }

    [Fact]
    public async Task CreateContentDocumentAsync_writes_a_permanent_doc_in_the_archive_keyspace()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var bytes = Encoding.UTF8.GetBytes("""[{"icao":"LSZH"}]""");

        var id = await rig.Facade.CreateContentDocumentAsync(rig.RootId, TestModule.TestModule.EntryMaskId, "ICAO list", bytes, "json");

        using var check = Ctx(connection, rig.TenantId);
        var doc = await check.Documents.SingleAsync(d => d.Id == id);
        Assert.Null(doc.ExpiresAt); // permanent — the sweep never touches it
        var version = await check.DocumentVersions.SingleAsync(v => v.DocumentId == id);
        Assert.DoesNotContain("/fs/special/", version.ObjectKey); // the archive keyspace, not the staging store
    }

    [Fact]
    public async Task Replace_in_place_adds_a_version_and_refreshes_expiry_and_name()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var id = await rig.Facade.StageContentAsync(
            rig.RootId, TestModule.TestModule.EntryMaskId, "METAR 1200Z", Encoding.UTF8.GetBytes("v1"), "txt", DateTimeOffset.UtcNow.AddHours(2));

        var e2 = DateTimeOffset.UtcNow.AddHours(4);
        var again = await rig.Facade.StageContentAsync(
            rig.RootId, TestModule.TestModule.EntryMaskId, "METAR 1230Z", Encoding.UTF8.GetBytes("v2"), "txt", e2, replaceDocumentId: id);

        Assert.Equal(id, again); // same document, replaced in place — never two entries in the folder
        using var check = Ctx(connection, rig.TenantId);
        var doc = await check.Documents.SingleAsync(d => d.Id == id);
        Assert.Equal("METAR 1230Z", doc.Name);
        Assert.Equal(e2, doc.ExpiresAt);
        Assert.Equal(2, await check.DocumentVersions.CountAsync(v => v.DocumentId == id));
        Assert.Equal(Encoding.UTF8.GetBytes("v2"), await rig.Facade.GetDocumentContentAsync(id)); // current shows the fresh bytes
    }

    [Fact]
    public async Task The_sweep_purges_expired_staged_content_and_keeps_the_unexpired_and_permanent()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        var expired = await rig.Facade.StageContentAsync(
            rig.RootId, TestModule.TestModule.EntryMaskId, "old", Encoding.UTF8.GetBytes("old"), "txt", DateTimeOffset.UtcNow.AddHours(-1));
        var fresh = await rig.Facade.StageContentAsync(
            rig.RootId, TestModule.TestModule.EntryMaskId, "new", Encoding.UTF8.GetBytes("new"), "txt", DateTimeOffset.UtcNow.AddHours(1));
        var permanent = await rig.Facade.CreateContentDocumentAsync(
            rig.RootId, TestModule.TestModule.EntryMaskId, "ref", Encoding.UTF8.GetBytes("ref"), "json");
        var expiredKey = await KeyOf(connection, rig.TenantId, expired);

        var swept = await MakeSweeper(connection, rig.Storage).SweepAsync(default);

        Assert.Equal(1, swept);
        using var check = Ctx(connection, rig.TenantId);
        Assert.False(await check.Documents.AnyAsync(d => d.Id == expired));  // purged
        Assert.True(await check.Documents.AnyAsync(d => d.Id == fresh));     // not yet due
        Assert.True(await check.Documents.AnyAsync(d => d.Id == permanent)); // never touched (no expiry)
        Assert.False(rig.Storage.Objects.ContainsKey(expiredKey));          // its object went too
    }

    private static async Task<string> KeyOf(SqliteConnection connection, Guid tenantId, Guid documentId)
    {
        using var check = Ctx(connection, tenantId);
        return await check.DocumentVersions.Where(v => v.DocumentId == documentId).Select(v => v.ObjectKey).SingleAsync();
    }

    private static EphemeralContentSweepWorker MakeSweeper(SqliteConnection connection, InMemoryObjectStorage storage)
    {
        var services = new ServiceCollection();
        services.AddScoped<SimplArchiveDbContext>(_ => Ctx(connection)); // filters off in the sweep, so a null tenant is fine
        services.AddSingleton<IObjectStorageClient>(storage);
        var provider = services.BuildServiceProvider();
        return new EphemeralContentSweepWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<EphemeralContentSweepWorker>.Instance);
    }
}
