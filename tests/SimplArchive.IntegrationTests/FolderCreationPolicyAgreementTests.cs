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

// `FolderCreationPolicy` decides whether the Api advertises the `folders` rel, and three separate SaveChanges
// invariants decide whether the create actually succeeds. Two places answering one question is drift waiting to
// happen, and the drift is invisible from either side: a predicate that is too strict HIDES an action that
// works, one that is too loose OFFERS an action that cannot.
//
// So this asks both, for every well-known folder mask, and asserts they agree — the predicate against a real
// save. Adding a folder mask, a typed-folder rule or a no-subfolder rule extends this automatically, because
// the cases come from WellKnownMaskIds rather than from a list written out here.
public class FolderCreationPolicyAgreementTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    public static TheoryData<string> FolderMasks()
    {
        var data = new TheoryData<string>();
        foreach (var maskId in WellKnownMaskIds.FolderMasks)
        {
            data.Add(maskId.ToString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FolderMasks))]
    public async Task The_rel_predicate_agrees_with_what_SaveChanges_allows(string maskIdText)
    {
        var parentMaskId = Guid.Parse(maskIdText);

        // A Repository or a User Folder is a ROOT — it cannot be created as somebody's child, so there is no
        // "parent wearing this mask" to test. The personal-root case is covered separately below.
        if (parentMaskId == WellKnownMaskIds.Repository || parentMaskId == WellKnownMaskIds.UserFolder)
        {
            return;
        }

        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var _c = connection;
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        Guid userId, repoId;
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "u@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);

            var repo = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                Name = $"Repo {Guid.NewGuid():N}"[..12],
                MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.Repository, CancellationToken.None),
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Documents.Add(repo);
            await db.SaveChangesAsync();
            repoId = repo.Id;
        }

        // The parent, wearing the mask under test. Some masks may not live under a repository either, in which
        // case there is nothing to ask — the rel question only arises where the parent can exist at all.
        Guid parentId;
        using (var db = Ctx(connection, accessor))
        {
            var parent = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ParentId = repoId,
                Name = "Parent",
                MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, parentMaskId, CancellationToken.None),
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Documents.Add(parent);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (InvalidOperationException)
            {
                return; // this mask cannot be a child of a repository; no rel question to answer
            }

            parentId = parent.Id;
        }

        var predicted = FolderCreationPolicy.AdmitsPlainFolder(parentMaskId, parentIsPersonalRoot: false);

        bool actual;
        using (var db = Ctx(connection, accessor))
        {
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ParentId = parentId,
                Name = "A plain folder",
                MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.Folder, CancellationToken.None),
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            try
            {
                await db.SaveChangesAsync();
                actual = true;
            }
            catch (InvalidOperationException)
            {
                actual = false;
            }
        }

        Assert.True(
            predicted == actual,
            $"The `folders` rel would {(predicted ? "be advertised" : "be withheld")} inside a folder wearing "
            + $"{parentMaskId}, but SaveChanges {(actual ? "allows" : "refuses")} the create. A rel that "
            + "disagrees with the invariant either hides an action that works or offers one that cannot.");
    }

    [Fact]
    public async Task And_agrees_on_the_personal_space_first_level()
    {
        // Its own case because the parent is a ROOT, so it cannot be built by the theory above — and because it
        // is the one the rel was added for (#634).
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var _c = connection;
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        Guid userId;
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "p@t.test", DisplayName = "P", CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);
        }

        Guid personalId;
        using (var db = Ctx(connection, accessor))
        {
            personalId = (await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance)
                .EnsureAsync(userId, _tenantId, CancellationToken.None)).Id;
        }

        var predicted = FolderCreationPolicy.AdmitsPlainFolder(WellKnownMaskIds.UserFolder, parentIsPersonalRoot: true);

        bool actual;
        using (var db = Ctx(connection, accessor))
        {
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ParentId = personalId,
                Name = "A plain folder",
                MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.Folder, CancellationToken.None),
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            try
            {
                await db.SaveChangesAsync();
                actual = true;
            }
            catch (InvalidOperationException)
            {
                actual = false;
            }
        }

        Assert.False(actual);
        Assert.Equal(actual, predicted);
    }
}
