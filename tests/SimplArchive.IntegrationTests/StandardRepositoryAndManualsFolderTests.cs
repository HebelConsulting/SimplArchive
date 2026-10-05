using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The two SaveChanges invariants the manuals feature rests on (ADRs 0891/0892): the tenant's standard repository
// cannot be deleted, and the manuals folder's "SimplArchive Documentation" mask is the core's alone — assigned only
// through an explicit grant, never changed or removed, worn by at most one live folder per tenant.
public class StandardRepositoryAndManualsFolderTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    private async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor, Guid UserId, Guid StandardId, Guid OtherId)> TenantAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        using var db = Ctx(connection, accessor);
        var tenant = new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow };
        db.Tenants.Add(tenant);
        var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "u@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);

        var standard = Root("Standard", user.Id);
        var other = Root("Other", user.Id);
        db.Documents.AddRange(standard, other);
        await db.SaveChangesAsync();
        tenant.StandardRepositoryId = standard.Id;
        await db.SaveChangesAsync();
        return (connection, accessor, user.Id, standard.Id, other.Id);
    }

    private Document Root(string name, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        Name = name,
        CreatedByUserId = userId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Task<Guid> MaskVersionAsync(SimplArchiveDbContext db, Guid maskId) =>
        db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).SingleAsync();

    private async Task<Document> ManualsFolderAsync(SimplArchiveDbContext db, Guid parentId, Guid userId, bool permitted = true, string name = "SimplArchive Manuals")
    {
        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Documentation),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (permitted)
        {
            db.PermitCoreOwnedMask(folder.Id);
        }

        db.Documents.Add(folder);
        return folder;
    }

    [Fact]
    public async Task The_standard_repository_cannot_go_to_the_recycle_bin()
    {
        var (connection, accessor, _, standardId, _) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);

        (await db.Documents.SingleAsync(d => d.Id == standardId)).DeletedAt = DateTimeOffset.UtcNow;

        var refused = await Assert.ThrowsAsync<StandardRepositoryProtectedException>(() => db.SaveChangesAsync());
        Assert.Contains("Make another repository the standard one first", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_standard_repository_cannot_be_hard_deleted_either()
    {
        var (connection, accessor, _, standardId, _) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);

        db.Documents.Remove(await db.Documents.SingleAsync(d => d.Id == standardId));

        await Assert.ThrowsAsync<StandardRepositoryProtectedException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Another_repository_is_deleted_as_before()
    {
        // The anti-vacuous half: the rule is about ONE document, not about repositories.
        var (connection, accessor, _, _, otherId) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);

        (await db.Documents.SingleAsync(d => d.Id == otherId)).DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Only_a_granted_document_may_be_given_the_manuals_folder_mask()
    {
        var (connection, accessor, userId, standardId, _) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);

        await ManualsFolderAsync(db, standardId, userId, permitted: false);

        var refused = await Assert.ThrowsAsync<CoreOwnedMaskException>(() => db.SaveChangesAsync());
        Assert.Contains("only SimplArchive itself assigns it", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_folder_cannot_be_retyped_to_the_manuals_folder_mask()
    {
        var (connection, accessor, userId, standardId, _) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);

        var plain = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = standardId,
            Name = "Mine",
            MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Folder),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(plain);
        await db.SaveChangesAsync();

        plain.MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Documentation);
        await Assert.ThrowsAsync<CoreOwnedMaskException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_manuals_folder_cannot_lose_or_change_its_mask()
    {
        var (connection, accessor, userId, standardId, _) = await TenantAsync();
        using var _c = connection;

        Guid folderId;
        using (var db = Ctx(connection, accessor))
        {
            folderId = (await ManualsFolderAsync(db, standardId, userId)).Id;
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            (await db.Documents.SingleAsync(d => d.Id == folderId)).MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Folder);
            var refused = await Assert.ThrowsAsync<CoreOwnedMaskException>(() => db.SaveChangesAsync());
            Assert.Contains("cannot be changed or removed", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task There_is_only_ever_one_live_manuals_folder()
    {
        var (connection, accessor, userId, standardId, otherId) = await TenantAsync();
        using var _c = connection;

        using (var db = Ctx(connection, accessor))
        {
            await ManualsFolderAsync(db, standardId, userId);
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            // Even granted: a second one would make "the manuals folder" ambiguous.
            await ManualsFolderAsync(db, otherId, userId);
            var refused = await Assert.ThrowsAsync<CoreOwnedMaskException>(() => db.SaveChangesAsync());
            Assert.Contains("only ever one", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_manuals_folder_admits_manuals_and_nothing_else_while_a_manual_may_live_anywhere()
    {
        var (connection, accessor, userId, standardId, otherId) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);

        var folder = await ManualsFolderAsync(db, standardId, userId);
        await db.SaveChangesAsync();

        var manualMask = await MaskVersionAsync(db, WellKnownMaskIds.Manual);
        db.Documents.Add(new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = folder.Id, Name = "A manual", MaskVersionId = manualMask, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        // One-way: a third-party manual beside its invoice, outside the manuals folder, is fine.
        db.Documents.Add(new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = otherId, Name = "Kettle manual", MaskVersionId = manualMask, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = folder.Id,
            Name = "An invoice",
            MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.BasicEntry),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
    }
}
