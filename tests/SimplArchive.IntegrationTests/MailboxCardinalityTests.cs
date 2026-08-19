using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A personal space holds at most ONE mailbox (ADR 0628, #596). This is a CAPACITY rule on the folder, not a
// placement rule on the mailbox: a personal space still holds whatever else its owner puts there, and the only
// thing it never holds is a second mailbox. Those two readings need different code, so the first test below
// pins the distinction rather than leaving it to the rule table's comment.
public class MailboxCardinalityTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task<SqliteConnection> TenantAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new SimplArchive.Domain.Users.User
            {
                Id = _ownerId,
                TenantId = _tenantId,
                Email = "owner@t.test",
                DisplayName = "Owner",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        return connection;
    }

    private async Task<Guid> MaskVersionAsync(SqliteConnection connection, Guid maskId)
    {
        using var db = Ctx(connection);
        return await db.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == _tenantId && v.MaskId == maskId && v.IsCurrent)
            .Select(v => v.Id)
            .SingleAsync();
    }

    private Document Doc(string name, Guid? parentId, Guid maskVersionId, Guid? personalOf = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        ParentId = parentId,
        Name = name,
        MaskVersionId = maskVersionId,
        PersonalOfUserId = personalOf,
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedByUserId = _ownerId,
    };

    private async Task<(SqliteConnection Connection, Guid PersonalId, Guid MailboxVersionId, Guid FolderVersionId)>
        PersonalSpaceAsync()
    {
        var connection = await TenantAsync();
        var userFolderVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.UserFolder);
        var mailboxVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.Mailbox);
        var folderVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.Folder);

        var personal = Doc("Personal", null, userFolderVersionId, personalOf: _ownerId);
        using (var db = Ctx(connection))
        {
            db.Documents.Add(personal);
            await db.SaveChangesAsync();
        }

        return (connection, personal.Id, mailboxVersionId, folderVersionId);
    }

    [Fact]
    public async Task A_personal_space_still_admits_ordinary_documents()
    {
        // The capacity rule must not have quietly turned the personal space into a TYPED folder. If it had, this
        // is what would break — and it would break for every user, not just one with a mailbox.
        var (connection, personalId, _, folderVersionId) = await PersonalSpaceAsync();
        using var _c = connection;

        using (var db = Ctx(connection))
        {
            db.Documents.Add(Doc("Invoices", personalId, folderVersionId));
            db.Documents.Add(Doc("Notes to self", personalId, folderVersionId));
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            Assert.Equal(2, await db.Documents.CountAsync(d => d.ParentId == personalId));
        }
    }

    [Fact]
    public async Task A_second_mailbox_is_refused()
    {
        var (connection, personalId, mailboxVersionId, _) = await PersonalSpaceAsync();
        using var _c = connection;

        using (var db = Ctx(connection))
        {
            db.Documents.Add(Doc("Shared Mailbox", personalId, mailboxVersionId));
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            db.Documents.Add(Doc("Other eMails", personalId, mailboxVersionId));
            var thrown = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());

            // The message must say the slot is TAKEN, not that the folder refuses mailboxes — those send the
            // reader after two different mistakes and only one of them is real.
            Assert.Contains("already there", thrown.Message);
        }
    }

    [Fact]
    public async Task Two_mailboxes_added_in_ONE_save_do_not_both_slip_through()
    {
        // Each would see an empty folder if the check only looked at the database: neither is persisted yet.
        var (connection, personalId, mailboxVersionId, _) = await PersonalSpaceAsync();
        using var _c = connection;

        using (var db = Ctx(connection))
        {
            db.Documents.Add(Doc("First", personalId, mailboxVersionId));
            db.Documents.Add(Doc("Second", personalId, mailboxVersionId));
            await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        }

        using (var db = Ctx(connection))
        {
            Assert.Equal(0, await db.Documents.CountAsync(d => d.ParentId == personalId));
        }
    }

    [Fact]
    public async Task Deleting_the_mailbox_frees_the_slot_immediately()
    {
        // "Only live ones count" — the same reading sibling-name uniqueness already takes, so a user who deletes
        // their mailbox is not blocked by a row they can no longer see.
        var (connection, personalId, mailboxVersionId, _) = await PersonalSpaceAsync();
        using var _c = connection;

        var first = Doc("Shared Mailbox", personalId, mailboxVersionId);
        using (var db = Ctx(connection))
        {
            db.Documents.Add(first);
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            var existing = await db.Documents.SingleAsync(d => d.Id == first.Id);
            existing.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            db.Documents.Add(Doc("Shared Mailbox", personalId, mailboxVersionId));
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            Assert.Equal(1, await db.Documents.CountAsync(d => d.ParentId == personalId));
        }
    }

    [Fact]
    public async Task Restoring_a_deleted_mailbox_beside_a_replacement_is_refused()
    {
        // The hole that "only live ones count" opens, and the reason the check belongs in SaveChanges: a restore
        // is a save too, so it lands on the same invariant instead of needing a rule of its own on the restore
        // path — which is where a second copy of this logic would have gone, and drifted.
        var (connection, personalId, mailboxVersionId, _) = await PersonalSpaceAsync();
        using var _c = connection;

        var original = Doc("Shared Mailbox", personalId, mailboxVersionId);
        using (var db = Ctx(connection))
        {
            db.Documents.Add(original);
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            (await db.Documents.SingleAsync(d => d.Id == original.Id)).DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            db.Documents.Add(Doc("Replacement", personalId, mailboxVersionId));
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            var deleted = await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"])
                .SingleAsync(d => d.Id == original.Id);
            deleted.DeletedAt = null;

            await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_mailbox_moved_out_frees_its_slot_in_the_same_save()
    {
        // The change tracker holds the INTENDED state, so a sibling on its way out must stop occupying the slot
        // even though the database still shows it there. Counting the database and the tracker separately and
        // adding them would refuse this — a swap in one transaction is a legitimate thing to do.
        var (connection, personalId, mailboxVersionId, folderVersionId) = await PersonalSpaceAsync();
        using var _c = connection;

        var elsewhere = Doc("Archive", null, folderVersionId);
        var existing = Doc("Shared Mailbox", personalId, mailboxVersionId);
        using (var db = Ctx(connection))
        {
            db.Documents.Add(elsewhere);
            db.Documents.Add(existing);
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            var moving = await db.Documents.SingleAsync(d => d.Id == existing.Id);
            moving.ParentId = elsewhere.Id;
            db.Documents.Add(Doc("Fresh", personalId, mailboxVersionId));
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            Assert.Equal("Fresh", (await db.Documents.SingleAsync(d => d.ParentId == personalId)).Name);
        }
    }
}
