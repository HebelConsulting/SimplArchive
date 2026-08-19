using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The upgrade-path defect first seen as the demo stack's maskless Personal/Notes folder (#562 slice 5): a
// tenant provisioned BEFORE a typed mask existed has no mask to resolve, so the folder is created with
// MaskVersionId = null — in which state it neither projects onto its protocol surface nor enforces its typed
// containment. The fix is two-sided: Program.cs backfills well-known masks for every existing tenant at
// startup, and the provisioner heals an already-created maskless folder on the next ensure. This covers the heal.
//
// Written against the ADDRESSBOOK since 2026-08-19: the Notebook it originally used is no longer provisioned
// in the personal space, but the heal it guards is unchanged and still runs for every typed folder.
public class PersonalTypedFolderMaskHealTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    [Fact]
    public async Task Maskless_typed_folder_is_healed_once_the_mask_is_seeded()
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

        // Provision with NO well-known masks seeded — the pre-upgrade tenant. Notes comes out maskless.
        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);
            var notes = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.MyAddressbookFolderName);
            Assert.Null(notes.MaskVersionId);
        }

        // The upgrade arrives: the startup backfill seeds the well-known masks for the existing tenant.
        using (var db = Ctx(connection, accessor))
        {
            await new WellKnownMaskSeeder(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Masks.WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);
        }

        // The next ensure heals the folder onto the Notebook mask's current version.
        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);
        }

        Guid? healedMaskVersionId;
        using (var db = Ctx(connection, accessor))
        {
            var notes = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.MyAddressbookFolderName);
            healedMaskVersionId = notes.MaskVersionId;
            var maskId = await db.MaskVersions
                .Where(v => v.Id == notes.MaskVersionId)
                .Select(v => (Guid?)v.MaskId)
                .SingleOrDefaultAsync();
            Assert.Equal(WellKnownMaskIds.Addressbook, maskId);
        }

        // Idempotent: a further ensure leaves the healed assignment untouched.
        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);
            var notes = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.MyAddressbookFolderName);
            Assert.Equal(healedMaskVersionId, notes.MaskVersionId);
        }
    }

    // The OTHER half of the same upgrade: #564 renamed the folder "Notes" → "Notebook" along with its mask.
    // A provisioner that looked only for the new name would leave the old folder — the one holding the user's
    // notes — sitting beside a fresh empty one, which is #574's trap exactly: a grow-later seed verified only
    // against a fresh volume, where the stranding case cannot occur.
}
