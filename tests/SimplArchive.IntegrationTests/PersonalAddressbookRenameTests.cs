using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The upgrade path for `My Contacts` → `My Addressbook` (ADR 0632). The provisioner get-or-creates by NAME and
// is idempotent, so changing the constant alone would find nothing called `My Addressbook` in a space that was
// already provisioned and helpfully create it — leaving the user an empty new folder beside the one holding
// their contacts, with their CardDAV client still subscribed to the old one.
//
// This is the #574 trap, and it is invisible to the rest of the suite by construction: every E2E run
// provisions a FRESH tenant, where no legacy folder exists and the rename branch never executes. The only way
// it is covered is a test that deliberately builds the pre-rename state.
public class PersonalAddressbookRenameTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    [Fact]
    public async Task An_already_provisioned_space_is_renamed_rather_than_given_a_second_folder()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
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
        }

        // Provision once, then rename the folder BACK to what it was called before 2026-08-19 — which is
        // exactly the state every deployment provisioned before then is in.
        Guid addressbookId;
        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);
            var folder = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.MyAddressbookFolderName);
            folder.Name = PersonalRepositoryProvisioner.LegacyMyContactsFolderName;
            await db.SaveChangesAsync();
            addressbookId = folder.Id;
        }

        // The next ensure — a restart, or any personal-repository read — must migrate it.
        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);

            var named = await db.Documents
                .Where(d => d.Name == PersonalRepositoryProvisioner.MyAddressbookFolderName
                            || d.Name == PersonalRepositoryProvisioner.LegacyMyContactsFolderName)
                .ToListAsync();

            // Renamed IN PLACE: the SAME document, so the contacts inside it and the collection a client is
            // subscribed to both come along. A second folder here is the whole failure this guards.
            var single = Assert.Single(named);
            Assert.Equal(PersonalRepositoryProvisioner.MyAddressbookFolderName, single.Name);
            Assert.Equal(addressbookId, single.Id);
        }
    }

    [Fact]
    public async Task The_contacts_inside_it_survive_the_rename()
    {
        // Renaming in place is only worth anything if what the folder HOLDS is still there afterwards — the
        // failure mode being migrated away from is a user whose contacts are in a folder nothing points at.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        Guid userId;
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "u2@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        Guid contactId;
        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);
            var folder = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.MyAddressbookFolderName);
            folder.Name = PersonalRepositoryProvisioner.LegacyMyContactsFolderName;

            var contact = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ParentId = folder.Id,
                Name = "Anna Meyer",
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Documents.Add(contact);
            await db.SaveChangesAsync();
            contactId = contact.Id;
        }

        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);

            var renamed = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.MyAddressbookFolderName);
            var contact = await db.Documents.SingleAsync(d => d.Id == contactId);
            Assert.Equal(renamed.Id, contact.ParentId);
        }
    }
}
