using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Every document wears a mask, and one it did not choose is decided by its own SHAPE (#1240).
//
// WHY THE STATE WENT AWAY. "No mask" was never a legitimate answer: a document without a mask has no index
// fields, so the drop-down offering it let a user discard a document's typing with one click and gain nothing.
// It was reachable from four places and only TWO were a user choosing — the others were an importer's transient
// state before phase D, and the finaliser's fallback when staged index data failed to validate. Neither is a
// decision anybody made.
//
// WHY THE DEFAULT IS BY SHAPE rather than per call site: the call sites disagreed, which is how the state
// survived. Basic Entry where the document has content, Folder where it has none — an answer that depends on
// what the document IS, so a path that forgets still produces something sensible.
public class EveryDocumentWearsAMaskTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task<(SqliteConnection Connection, Guid UserId)> WorldAsync(bool seedMasks)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        using var db = Ctx(connection);
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "u@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        if (seedMasks)
        {
            await new WellKnownMaskSeeder(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        return (connection, user.Id);
    }

    private static Document Untyped(Guid tenantId, Guid userId, string name) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = name,
        MaskVersionId = Guid.Empty,
        CreatedByUserId = userId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<Guid> MaskIdOfAsync(SimplArchiveDbContext db, Guid documentId)
    {
        var versionId = await db.Documents.Where(d => d.Id == documentId).Select(d => d.MaskVersionId).SingleAsync();
        Assert.NotEqual(Guid.Empty, versionId);
        return await db.MaskVersions.Where(v => v.Id == versionId).Select(v => v.MaskId).SingleAsync();
    }

    [Fact]
    public async Task A_document_with_content_is_typed_as_a_basic_entry()
    {
        var (connection, userId) = await WorldAsync(seedMasks: true);
        using var _c = connection;
        using var db = Ctx(connection);

        // The document and its version in ONE SaveChanges, which is what DocumentFinalizer does. Asked as a
        // QUERY, "does it have versions?" answers false here — the version row is not committed yet — and the
        // document would be typed as a Folder. The invariant has to count Added entries too, and this is the
        // test that fails if it stops.
        var document = Untyped(_tenantId, userId, "Invoice.pdf");
        db.Documents.Add(document);
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            DocumentId = document.Id,
            ObjectKey = "k",
            Status = DocumentVersionStatus.Pending,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(WellKnownMaskIds.BasicEntry, await MaskIdOfAsync(db, document.Id));
    }

    [Fact]
    public async Task A_document_with_no_content_is_typed_as_a_folder()
    {
        var (connection, userId) = await WorldAsync(seedMasks: true);
        using var _c = connection;
        using var db = Ctx(connection);

        var folder = Untyped(_tenantId, userId, "Invoices");
        db.Documents.Add(folder);
        await db.SaveChangesAsync();

        Assert.Equal(WellKnownMaskIds.Folder, await MaskIdOfAsync(db, folder.Id));
    }

    [Fact]
    public async Task The_sentinel_never_survives_a_save_even_on_an_update()
    {
        var (connection, userId) = await WorldAsync(seedMasks: true);
        using var _c = connection;
        using var db = Ctx(connection);

        var folder = Untyped(_tenantId, userId, "Invoices");
        db.Documents.Add(folder);
        await db.SaveChangesAsync();

        // Writing the sentinel back is the nearest thing to "clear the mask" that still exists at this layer.
        // It must not clear anything — there is no path back to an untyped document.
        folder.MaskVersionId = Guid.Empty;
        await db.SaveChangesAsync();

        Assert.NotEqual(Guid.Empty, (await db.Documents.SingleAsync(d => d.Id == folder.Id)).MaskVersionId);
    }

    [Fact]
    public async Task A_tenant_whose_masks_are_missing_gets_them_rather_than_a_refusal()
    {
        // SHOULD NEVER FIRE in production — the masks are seeded at provisioning and healed at startup
        // (ADR 0757). It exists because the cost of being wrong is asymmetric: refusing instead would mean
        // refusing every write for that tenant, which is the shape that boot-crashed the v0.13→v0.14 upgrade
        // when a stricter seed met real data.
        var (connection, userId) = await WorldAsync(seedMasks: false);
        using var _c = connection;
        using var db = Ctx(connection);

        Assert.False(await db.MaskVersions.IgnoreQueryFilters(["TenantFilter"]).AnyAsync());

        var folder = Untyped(_tenantId, userId, "Invoices");
        db.Documents.Add(folder);
        await db.SaveChangesAsync();

        Assert.Equal(WellKnownMaskIds.Folder, await MaskIdOfAsync(db, folder.Id));
    }

    [Fact]
    public async Task Two_untyped_documents_in_one_save_share_the_mask_that_was_created_for_them()
    {
        // The duplicate the ChangeTracker check exists to prevent: without it each document adds its own copy
        // of the same well-known mask, and the second insert violates the primary key — so a perfectly ordinary
        // two-document save would fail, and only when the masks happened to be missing.
        var (connection, userId) = await WorldAsync(seedMasks: false);
        using var _c = connection;
        using var db = Ctx(connection);

        var a = Untyped(_tenantId, userId, "A");
        var b = Untyped(_tenantId, userId, "B");
        db.Documents.AddRange(a, b);
        await db.SaveChangesAsync();

        var maskA = await db.Documents.Where(d => d.Id == a.Id).Select(d => d.MaskVersionId).SingleAsync();
        var maskB = await db.Documents.Where(d => d.Id == b.Id).Select(d => d.MaskVersionId).SingleAsync();
        Assert.Equal(maskA, maskB);
        Assert.Single(await db.MaskVersions.IgnoreQueryFilters(["TenantFilter"]).ToListAsync());
    }
}
