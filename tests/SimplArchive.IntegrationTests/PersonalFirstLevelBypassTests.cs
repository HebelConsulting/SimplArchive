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
// in a later phase), and the shape a WebDAV PUT created until #644.
//
// That two-step used to walk straight past the first-level rule, because the rule was ARRIVAL-gated: it ran
// for Added documents and for a changed ParentId, and a later mask assignment is neither. Arrival-gating was
// not an oversight — it protected the HEAL, since a pre-upgrade space holds maskless folders and a rule that
// re-validated them on modification would have refused the very writes that fix them (ADR 0633's third wrong
// shape).
//
// #644 closes the bypass without re-breaking that, and the distinction is the point of this file: a heal
// assigns an ADMITTED mask, so it PASSES the check rather than being exempted from it. Only an assignment the
// level would have refused on arrival is refused now — the same question, asked at the moment the answer
// becomes knowable.
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
    public async Task A_later_mask_assignment_is_refused_like_an_arrival_would_have_been()
    {
        // The bypass, closed (#644). This test asserted the OPPOSITE until then — it documented the hole as a
        // fact rather than a bug, which is why flipping it is the deliverable.
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var document = await ArriveMasklessAsync(db, personalId, userId, "Slipped in");

        document.MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.BasicEntry, CancellationToken.None);

        await Assert.ThrowsAsync<PersonalSpaceStructureException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task But_the_heal_still_works_because_it_assigns_an_ADMITTED_mask()
    {
        // The half that would break if the new trigger were an exemption-shaped fix instead of a check. A
        // pre-upgrade space holds maskless folders, and healing one is a mask assignment on a document already
        // sitting at the first level — indistinguishable, mechanically, from the bypass above. What separates
        // them is WHICH mask: My Documents is admitted there, Basic Entry is not.
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var folder = await ArriveMasklessAsync(db, personalId, userId, "A pre-upgrade folder");

        folder.MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.MyDocuments, CancellationToken.None);
        await db.SaveChangesAsync();

        var stored = await db.Documents.SingleAsync(d => d.Id == folder.Id);
        Assert.Equal(personalId, stored.ParentId);
        Assert.NotNull(stored.MaskVersionId);
    }

    [Fact]
    public async Task And_a_mask_assignment_somewhere_ELSE_is_untouched()
    {
        // The rule is about the personal space's first level and nothing else. A document inside My Documents
        // is masked by the finalizer on every upload, and a guard that reached it would refuse every file the
        // user ever adds.
        var (connection, accessor, userId, personalId) = await SpaceAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var myDocumentsId = await db.Documents
            .Where(d => d.ParentId == personalId && d.Name == PersonalFolders.MyDocuments)
            .Select(d => d.Id).SingleAsync();

        var document = await ArriveMasklessAsync(db, myDocumentsId, userId, "An ordinary upload");
        document.MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, WellKnownMaskIds.BasicEntry, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.NotNull((await db.Documents.SingleAsync(d => d.Id == document.Id)).MaskVersionId);
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
