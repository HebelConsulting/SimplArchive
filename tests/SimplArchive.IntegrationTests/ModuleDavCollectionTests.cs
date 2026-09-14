using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// A module declares a DAV collection kind (ABI 0.24, ADR 0791), and the SEAM that must recognise it end-to-end
// is the change recorder in SaveChanges: when a flight-log entry is filed into a module's Logbook, a
// DavCollectionChange has to be recorded, or a subscribed phone never learns of the new flight (no sync entry,
// no CTag bump, no doorbell). That recognition flows from whether the DbContext was handed the module-aware
// registry — so these pin BOTH sides: with the registry the change is recorded, and without it (the core-only
// fallback tests and the design-time factory use) it is not, which is exactly why the fallback is safe.
public class ModuleDavCollectionTests
{
    private static readonly Guid LogbookFolderMask = Guid.Parse("C0FFEE00-0000-0000-0000-0000000000F1");
    private static readonly Guid LogEntryMask = Guid.Parse("C0FFEE00-0000-0000-0000-0000000000E1");

    private static SimplArchiveDbContext CreateContext(
        SqliteConnection connection, Guid? tenantId = null, IDavCollectionKindRegistry? kinds = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options;
        return new SimplArchiveDbContext(
            options, new CurrentTenantAccessor { TenantId = tenantId },
            davCollectionKinds: kinds);
    }

    // A module whose Logbook folder is a read-only .ics collection, entries wearing LogEntryMask.
    private sealed class LogbookModule : IIndustryModule
    {
        public string ModuleId => "logbook-dav";
        public string DisplayName => "Logbook DAV module";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks =>
        [
            new ModuleMaskSeed(LogbookFolderMask, "Logbook", IsFolderMask: true, IsBookable: false, Fields: [])
            {
                DavCollection = new ModuleDavCollection(".ics", LogEntryMask, "Event UID", ReadOnly: true),
            },
            new ModuleMaskSeed(LogEntryMask, "Flight log entry", IsFolderMask: false, IsBookable: false, Fields: []),
        ];
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
    }

    private static IDavCollectionKindRegistry Registry(IIndustryModule module) =>
        new DavCollectionKindRegistry([new ModuleLoader.LoadedModule(module, "in-memory")]);

    [Fact]
    public async Task Filing_an_entry_into_a_module_logbook_records_a_dav_change_when_the_registry_knows_the_kind()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var (tenantId, userId, rootId) = await SeedTenantAndMasksAsync(connection);

        var folderId = await CreateLogbookFolderAsync(connection, tenantId, userId, rootId);

        // The registry-backed context recognises the module kind — filing an entry records the change.
        using (var db = CreateContext(connection, tenantId, Registry(new LogbookModule())))
        {
            await FileEntryAsync(db, tenantId, userId, folderId);
        }

        using var check = CreateContext(connection, tenantId);
        var changes = await check.DavCollectionChanges.Where(c => c.FolderId == folderId).ToListAsync();
        Assert.Single(changes);
        Assert.EndsWith(".ics", changes[0].ResourceName);
    }

    [Fact]
    public async Task Without_the_registry_the_core_fallback_does_not_recognise_the_module_kind()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var (tenantId, userId, rootId) = await SeedTenantAndMasksAsync(connection);

        var folderId = await CreateLogbookFolderAsync(connection, tenantId, userId, rootId);

        // No registry: the recorder falls back to the CORE kinds, which do not include the module's Logbook —
        // so nothing is recorded. This is the design-time factory / direct-construction path, and it must not
        // throw or invent changes; it simply serves what the core knows.
        using (var db = CreateContext(connection, tenantId))
        {
            await FileEntryAsync(db, tenantId, userId, folderId);
        }

        using var check = CreateContext(connection, tenantId);
        Assert.Empty(await check.DavCollectionChanges.Where(c => c.FolderId == folderId).ToListAsync());
    }

    private static async Task<(Guid TenantId, Guid UserId, Guid RootId)> SeedTenantAndMasksAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "m@example.com", DisplayName = "M", CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();

        using var masks = CreateContext(connection, tenantId);
        await new WellKnownMaskSeeder(masks, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
        await new ModuleMaskSeeder(masks, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new LogbookModule(), tenantId);
        return (tenantId, userId, rootId);
    }

    private static async Task<Guid> CreateLogbookFolderAsync(SqliteConnection connection, Guid tenantId, Guid userId, Guid rootId)
    {
        using var db = CreateContext(connection, tenantId);
        var maskVersionId = await db.MaskVersions.Where(v => v.MaskId == LogbookFolderMask && v.IsCurrent).Select(v => v.Id).SingleAsync();
        var folderId = Guid.NewGuid();
        db.Documents.Add(new Document
        {
            Id = folderId,
            TenantId = tenantId,
            ParentId = rootId,
            Name = "HB-PHG Logbook",
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            StorageFolderId = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return folderId;
    }

    // A flight-log entry: a document wearing the item mask plus a Pending version (the recorder fires on a
    // version add; a Pending version keeps VersionNumber/Sha256Hash null, which its CHECK constraint requires).
    private static async Task FileEntryAsync(SimplArchiveDbContext db, Guid tenantId, Guid userId, Guid folderId)
    {
        var maskVersionId = await db.MaskVersions.Where(v => v.MaskId == LogEntryMask && v.IsCurrent).Select(v => v.Id).SingleAsync();
        var documentId = Guid.NewGuid();
        db.Documents.Add(new Document
        {
            Id = documentId,
            TenantId = tenantId,
            ParentId = folderId,
            Name = "2026-09-14 HB-PHG",
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            StorageFolderId = Guid.NewGuid(),
        });
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = $"tenants/{tenantId}/2026/{Guid.NewGuid()}/content.ics",
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        await db.SaveChangesAsync();
    }
}
