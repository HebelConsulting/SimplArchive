using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The personal space's first level is closed (#596): the provisioned folders cannot be deleted or moved out,
// and the only thing that may join them is a plain Folder.
//
// Enforced in SaveChanges rather than per surface, because the personal space is written by the workbench,
// WebDAV, CalDAV/CardDAV, IMAP, LMTP, import and move — a check in any one of them is a check the other six
// skip. The protection half is the one with teeth today: nothing else stops a user deleting the calendar a
// CalDAV client is subscribed to.
public class PersonalSpaceStructureTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    private async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor, Guid UserId, Guid PersonalId)> SpaceAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        Guid userId;
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "u@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        Guid personalId;
        using (var db = Ctx(connection, accessor))
        {
            var root = await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance)
                .EnsureAsync(userId, _tenantId, CancellationToken.None);
            personalId = root.Id;
        }

        return (connection, accessor, userId, personalId);
    }

    [Theory]
    [InlineData(PersonalFolders.MyDocuments)]
    [InlineData(PersonalFolders.MyCalendar)]
    [InlineData(PersonalFolders.MyAddressbook)]
    public async Task A_provisioned_folder_cannot_be_soft_deleted(string name)
    {
        // Soft delete, not purge: a folder in the recycle bin is just as absent from the tree, and just as
        // gone from a subscribed client's point of view.
        var (connection, accessor, _, _) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var folder = await db.Documents.SingleAsync(d => d.Name == name);
        folder.DeletedAt = DateTimeOffset.UtcNow;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("cannot be deleted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provisioned_folder_cannot_be_moved_out_of_the_personal_space()
    {
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);

        // Somewhere else to move it TO — an ordinary folder inside the space.
        var elsewhere = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = personalId,
            Name = "Elsewhere",
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, CancellationToken.None),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(elsewhere);
        await db.SaveChangesAsync();

        var calendar = await db.Documents.SingleAsync(d => d.Name == PersonalFolders.MyCalendar);
        calendar.ParentId = elsewhere.Id;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("cannot be moved", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_folder_may_be_added_beside_them()
    {
        // The one change a user MAY make.
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = personalId,
            Name = "Tax 2026",
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, CancellationToken.None),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        Assert.True(await db.Documents.AnyAsync(d => d.Name == "Tax 2026"));
    }

    [Fact]
    public async Task A_typed_item_may_not_be_added_at_the_first_level()
    {
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        // A plain document, not a Note: a Note carries its OWN containment rule (it may only live in a
        // notebook), which would fire first and prove nothing about this one. Using the same mask as the
        // "deeper in the space" test below makes the pair a clean contrast — refused here, admitted there.
        var emailMaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.BasicEntry, CancellationToken.None);

        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = personalId,
            Name = "A loose document",
            MaskVersionId = emailMaskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("Only folders", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_maskless_folder_is_still_admitted_because_that_is_the_pre_upgrade_state()
    {
        // Not a loophole: a tenant provisioned before a mask existed holds maskless folders waiting to be
        // healed, and refusing them would make provisioning fail on exactly the deployments the heal repairs.
        // Found by the mask-heal test, not by reasoning about the rule.
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = personalId,
            Name = "Legacy folder",
            MaskVersionId = null,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        Assert.True(await db.Documents.AnyAsync(d => d.Name == "Legacy folder"));
    }

    [Fact]
    public async Task A_folder_deeper_in_the_space_is_unaffected()
    {
        // The rule closes the FIRST level only. Inside My Documents, anything goes — otherwise the fallback
        // that re-parents migrated content there would have nowhere to put it.
        var (connection, accessor, userId, _) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var myDocuments = await db.Documents.SingleAsync(d => d.Name == PersonalFolders.MyDocuments);
        var entryMaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.BasicEntry, CancellationToken.None);

        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = myDocuments.Id,
            Name = "A document that is fine here",
            MaskVersionId = entryMaskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        Assert.True(await db.Documents.AnyAsync(d => d.Name == "A document that is fine here"));
    }
}
