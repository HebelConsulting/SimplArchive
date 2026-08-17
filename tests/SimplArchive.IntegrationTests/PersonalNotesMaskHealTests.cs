using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The upgrade-path defect behind the demo stack's maskless Personal/Notes folder (#562 slice 5): a tenant
// provisioned BEFORE the NoteFolder mask existed has no mask for EnsureNotesAsync to resolve, so the folder
// was created with MaskVersionId = null — in which state it neither projects as the root "Notes" IMAP mailbox
// nor enforces its typed containment. The fix is two-sided: Program.cs backfills well-known masks for every
// existing tenant at startup, and EnsureNotesAsync heals an already-created maskless Notes folder on the next
// ensure. This covers the heal.
public class PersonalNotesMaskHealTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    [Fact]
    public async Task Maskless_notes_folder_is_healed_once_the_mask_is_seeded()
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
            var notes = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.NotesFolderName);
            Assert.Null(notes.MaskVersionId);
        }

        // The upgrade arrives: the startup backfill seeds the well-known masks for the existing tenant.
        using (var db = Ctx(connection, accessor))
        {
            await new WellKnownMaskSeeder(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Masks.WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);
        }

        // The next ensure heals the folder onto the NoteFolder mask's current version.
        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);
        }

        Guid? healedMaskVersionId;
        using (var db = Ctx(connection, accessor))
        {
            var notes = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.NotesFolderName);
            healedMaskVersionId = notes.MaskVersionId;
            var maskId = await db.MaskVersions
                .Where(v => v.Id == notes.MaskVersionId)
                .Select(v => (Guid?)v.MaskId)
                .SingleOrDefaultAsync();
            Assert.Equal(WellKnownMaskIds.NoteFolder, maskId);
        }

        // Idempotent: a further ensure leaves the healed assignment untouched.
        using (var db = Ctx(connection, accessor))
        {
            await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance).EnsureAsync(userId, _tenantId, CancellationToken.None);
            var notes = await db.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.NotesFolderName);
            Assert.Equal(healedMaskVersionId, notes.MaskVersionId);
        }
    }
}
