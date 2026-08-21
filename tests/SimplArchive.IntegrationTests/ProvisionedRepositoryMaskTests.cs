using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Provisioning;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Documents;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A repository is BORN wearing the Repository mask (ADR 0627) — not corrected into it afterwards.
//
// RepositoryMaskLockstepTests already covers the two halves that were thought about at the time: the backfill
// that moves a pre-existing Folder-masked root onto the mask, and the invariant that the two representations
// cannot contradict each other. Neither asks what this one asks, and the gap between them is where a real bug
// lived: TenantProvisioningService created the root with the FOLDER mask, because it called the two-argument
// FolderMask.CurrentVersionIdAsync overload whose default is Folder. RepositoriesController.Create did it
// correctly; the second creation path was never updated.
//
// It was invisible for a reason worth recording. The backfill runs at startup, BEFORE this service creates
// anything, so a repository born wrong was silently healed by the NEXT restart — and every long-lived
// installation restarts. The one deployment that never gets a second start is a freshly reset demo: the public
// kiosk does `down -v` nightly, so it served a repository drawn as a plain folder every single morning while
// every test and every developer machine looked fine. A test asserting the FINAL state after a restart would
// have passed too; this one asserts the state the provisioning call itself leaves behind.
public class ProvisionedRepositoryMaskTests
{
    [Fact]
    public async Task A_freshly_provisioned_repository_wears_the_repository_mask_without_a_restart()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setup = Ctx(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        using var db = Ctx(connection);
        var provisioned = await ServiceFor(db).ProvisionAsync(
            "Acme", "admin@acme.test", "Acme Admin", "Acme Repository", "Sup3rSecret!");

        var maskId = await MaskIdOfAsync(db, provisioned.RepositoryId);

        Assert.Equal(WellKnownMaskIds.Repository, maskId);

        // ...and the same call must not have turned every folder into a repository: the personal space the
        // administrator gets alongside it keeps User Folder (ADR 0590). Without this, handing the Repository
        // mask to a broader helper would satisfy the assertion above and quietly break the tree.
        var personalMaskIds = await db.Documents.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.TenantId == provisioned.TenantId && d.ParentId == null && d.Id != provisioned.RepositoryId)
            .Select(d => d.Id)
            .ToListAsync();

        Assert.NotEmpty(personalMaskIds); // anti-vacuous: provisioning really does create a personal space too
        foreach (var id in personalMaskIds)
        {
            Assert.Equal(WellKnownMaskIds.UserFolder, await MaskIdOfAsync(db, id));
        }
    }

    private static async Task<Guid?> MaskIdOfAsync(SimplArchiveDbContext db, Guid documentId)
    {
        var maskVersionId = await db.Documents.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.Id == documentId)
            .Select(d => d.MaskVersionId)
            .SingleAsync();

        return maskVersionId is null
            ? null
            : await db.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
                .Where(v => v.Id == maskVersionId.Value)
                .Select(v => (Guid?)v.MaskId)
                .SingleAsync();
    }

    // The real seeders, deliberately: substituting them would let the test agree with a mask catalogue that
    // production never builds. Only the two genuinely external things — object storage and the audit sink —
    // are stubbed.
    private static TenantProvisioningService ServiceFor(SimplArchiveDbContext db) =>
        new(db,
            new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance),
            new SensitivityLabelSeeder(db),
            new InMemoryObjectStorage(),
            new SimplArchive.Api.Documents.PersonalRepositoryProvisioner(db, new NoOpAuditRecorder()));

    // No current tenant: provisioning runs before one is known, which is why every lookup inside it ignores the
    // tenant filter (see FolderMaskTenantScopeTests for the same constraint from the other side).
    private static SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor());
}
