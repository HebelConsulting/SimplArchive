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

// What happens when a document arrives at a personal space's first level MASKLESS and is masked afterwards —
// the shape `RepositoryImporter` creates (it adds every document with MaskVersionId = null and assigns masks
// in a later phase).
//
// This matters because the two rules that guard that level answer differently, and the difference is not
// visible from either rule's own code:
//
//   the first-level rule   is ARRIVAL-gated (Added, or ParentId changed), so a later mask assignment is
//                          never re-checked — an import walks straight past it.
//   typed-folder containment is NOT gated: it runs for every Added OR Modified document, so the same later
//                          assignment IS checked, and refuses a mask whose AdmittingFolders say otherwise.
//
// So an import bypasses one rule and is stopped by the other, which is why "does import fail?" has no single
// answer. These tests pin both halves, because #630's fallback is built on top of exactly this asymmetry.
public class PersonalFirstLevelBypassTests
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
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);
        }

        Guid personalId;
        using (var db = Ctx(connection, accessor))
        {
            personalId = (await new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance)
                .EnsureAsync(userId, _tenantId, CancellationToken.None)).Id;
        }

        return (connection, accessor, userId, personalId);
    }

    private async Task<Document> ArriveMasklessAsync(SimplArchiveDbContext db, Guid parentId, Guid userId, string name)
    {
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = null,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    [Fact]
    public async Task A_later_mask_assignment_walks_past_the_first_level_rule()
    {
        // The bypass, stated as a test rather than as a claim. An eMail cannot be CREATED at the first level —
        // PersonalSpaceStructureTests pins that — but arriving maskless and being masked afterwards is a
        // different path, and the arrival gate does not see it.
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var document = await ArriveMasklessAsync(db, personalId, userId, "Slipped in");

        document.MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.BasicEntry, CancellationToken.None);
        await db.SaveChangesAsync();

        var stored = await db.Documents.SingleAsync(d => d.Id == document.Id);
        Assert.Equal(personalId, stored.ParentId);
        Assert.NotNull(stored.MaskVersionId);
    }

    [Fact]
    public async Task But_typed_folder_containment_still_refuses_it()
    {
        // …and this is why the bypass is not a free pass. A Notebook may only live under a Mailbox, and that
        // rule runs on MODIFY as well as on arrival — so the same two-step that smuggles a Basic Entry in is
        // refused for a Notebook.
        //
        // Which is a REGRESSION an importer meets in practice: `Personal/Notebook` was provisioned until
        // 2026-08-19, so any archive exported before then carries one at exactly this level.
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var document = await ArriveMasklessAsync(db, personalId, userId, "Notebook");

        document.MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.Notebook, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        Assert.Contains("can only live in a Mailbox", failure.Message, StringComparison.Ordinal);
    }
}
