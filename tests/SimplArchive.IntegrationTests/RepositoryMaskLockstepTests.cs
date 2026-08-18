using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A repository is a root document (ADR 0200) AND wears the Repository mask (ADR 0627) — kept in lockstep,
// which is the only thing that makes storing one fact twice safe.
//
// Two halves, and the upgrade half is the one that fails quietly. Every repository created before the mask
// existed wears Folder, and a seed that only ever grows would leave every pre-existing tenant behind — while a
// fresh-volume test sees nothing wrong, because the only tenants it creates are new ones. That is exactly how
// #574 was missed, so the backfill is tested against a repository that already exists.
public class RepositoryMaskLockstepTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    // Documents carry exactly one creator (CK_Documents_ExactlyOneCreator).
    private readonly Guid _creatorId = Guid.NewGuid();

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
                Id = _creatorId,
                TenantId = _tenantId,
                Email = "creator@t.test",
                DisplayName = "Creator",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return connection;
    }

    private async Task SeedMasksAsync(SqliteConnection connection)
    {
        using var db = Ctx(connection);
        await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance)
            .EnsureWellKnownMasksAsync(_tenantId);
    }

    private async Task<Guid> MaskVersionAsync(SqliteConnection connection, Guid maskId)
    {
        using var db = Ctx(connection);
        return await db.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == _tenantId && v.MaskId == maskId && v.IsCurrent)
            .Select(v => v.Id)
            .SingleAsync();
    }

    [Fact]
    public async Task A_repository_that_predates_the_mask_is_moved_onto_it()
    {
        using var connection = await TenantAsync();
        await SeedMasksAsync(connection);

        // A repository as it existed before ADR 0627: a root wearing the plain Folder mask.
        var folderVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.Folder);
        var repositoryId = Guid.NewGuid();
        using (var db = Ctx(connection))
        {
            db.Documents.Add(new Document
            {
                Id = repositoryId,
                TenantId = _tenantId,
                ParentId = null,
                Name = "Legacy Repository",
                MaskVersionId = folderVersionId,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = _creatorId,
            });
            await db.SaveChangesAsync();
        }

        // Seeding again is what a restart does. The backfill has to reach data that already exists — not just
        // stamp the masks and move on.
        await SeedMasksAsync(connection);

        var repositoryVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.Repository);
        using (var db = Ctx(connection))
        {
            var moved = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == repositoryId);
            Assert.Equal(repositoryVersionId, moved.MaskVersionId);
        }
    }

    [Fact]
    public async Task A_personal_space_and_a_child_folder_are_left_alone()
    {
        using var connection = await TenantAsync();
        await SeedMasksAsync(connection);

        var folderVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.Folder);
        var userFolderVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.UserFolder);
        var personalId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        using (var db = Ctx(connection))
        {
            // A personal space is also a root, and keeps User Folder (ADR 0590) — it is somebody's, and carries
            // their metadata. Sweeping every root onto Repository would erase that distinction.
            db.Documents.Add(new Document
            {
                Id = personalId,
                TenantId = _tenantId,
                ParentId = null,
                Name = "Personal",
                MaskVersionId = userFolderVersionId,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = _creatorId,
            });
            db.Documents.Add(new Document
            {
                Id = repositoryId,
                TenantId = _tenantId,
                ParentId = null,
                Name = "Repo",
                MaskVersionId = folderVersionId,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = _creatorId,
            });
            await db.SaveChangesAsync();

            // A child folder keeps Folder: the backfill is about ROOTS, and a folder inside a repository is
            // still just a folder.
            db.Documents.Add(new Document
            {
                Id = childId,
                TenantId = _tenantId,
                ParentId = repositoryId,
                Name = "Invoices",
                MaskVersionId = folderVersionId,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = _creatorId,
            });
            await db.SaveChangesAsync();
        }

        await SeedMasksAsync(connection);

        using (var db = Ctx(connection))
        {
            Assert.Equal(userFolderVersionId, (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == personalId)).MaskVersionId);
            Assert.Equal(folderVersionId, (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == childId)).MaskVersionId);
        }
    }

    [Fact]
    public async Task The_two_representations_cannot_contradict_each_other()
    {
        using var connection = await TenantAsync();
        await SeedMasksAsync(connection);

        var repositoryVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.Repository);
        var folderVersionId = await MaskVersionAsync(connection, WellKnownMaskIds.Folder);
        var repositoryId = Guid.NewGuid();

        using (var db = Ctx(connection))
        {
            db.Documents.Add(new Document
            {
                Id = repositoryId,
                TenantId = _tenantId,
                ParentId = null,
                Name = "Repo",
                MaskVersionId = repositoryVersionId,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = _creatorId,
            });
            await db.SaveChangesAsync();
        }

        // Lockstep is MAINTAINED, not vetoed. A root that acquires a parent has stopped being a repository, and
        // that is a legitimate operation — a bulk move with the manage-repositories right does exactly this — so
        // refusing it would block a supported action to protect a fact we can simply keep true. The mask is
        // corrected instead, at the single enforcement point, so every path inherits it.
        using (var db = Ctx(connection))
        {
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ParentId = repositoryId,
                Name = "Nested",
                MaskVersionId = repositoryVersionId,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = _creatorId,
            });

            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            var nested = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Name == "Nested");

            // It went in wearing Repository and came out wearing Folder — the two facts cannot disagree.
            Assert.Equal(folderVersionId, nested.MaskVersionId);
            Assert.NotEqual(repositoryVersionId, nested.MaskVersionId);
        }

        // …and the repository itself is untouched: correcting a child must not disturb the root it was filed
        // into, which is the assertion that keeps this from being satisfiable by stamping everything Folder.
        using (var db = Ctx(connection))
        {
            var root = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == repositoryId);
            Assert.Equal(repositoryVersionId, root.MaskVersionId);
        }
    }
}
