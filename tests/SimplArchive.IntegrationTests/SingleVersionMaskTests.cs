using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A Certificate document takes exactly ONE version (ADR 0848, WellKnownMaskIds.SingleVersionMasks).
//
// WHY THE RULE EXISTS, because a reader will otherwise assume it is arbitrary: the document IS the evidence of
// an enrolment — who filed it, when, when it was revoked — and a second version changes what that evidence is
// OF while keeping its history. It also removes a stale-index question rather than answering it: the finalizer
// re-extracts a new version's index fields for the DAV kinds only (ADR 0744), so version 2 of a certificate
// would be projected with version 1's Valid until. The open .eml case (#1466) is the same defect, undecided.
public class SingleVersionMaskTests
{
    private static SimplArchiveDbContext Context(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<(Guid TenantId, Guid UserId)> SeedAsync(SqliteConnection connection)
    {
        using (var setup = Context(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var seed = Context(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();

        using var masks = Context(connection, tenantId);
        await new WellKnownMaskSeeder(masks, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
        return (tenantId, userId);
    }

    private static async Task<Guid> FileAsync(
        SimplArchiveDbContext db, Guid tenantId, Guid userId, Guid maskId, string name)
    {
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            MaskVersionId = await db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).FirstAsync(),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document.Id;
    }

    private static DocumentVersion Version(Guid documentId, Guid tenantId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        DocumentId = documentId,
        TenantId = tenantId,
        Status = DocumentVersionStatus.Pending,
        ObjectKey = $"tenants/{tenantId}/2026/{Guid.NewGuid()}/content.pem",
        CreatedByUserId = userId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task The_FIRST_version_of_a_certificate_is_accepted()
    {
        // The rule is "no SECOND", not "none" — a certificate document with no version is unusable, so getting
        // this backwards would refuse every enrolment rather than every replacement.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        using var db = Context(connection, tenantId);
        var id = await FileAsync(db, tenantId, userId, WellKnownMaskIds.Certificate, "alice.pem");
        db.DocumentVersions.Add(Version(id, tenantId, userId));

        await db.SaveChangesAsync();   // no throw IS the assertion

        Assert.Equal(1, await db.DocumentVersions.CountAsync(v => v.DocumentId == id));
    }

    [Fact]
    public async Task The_first_version_filed_IN_THE_SAME_SAVE_as_the_document_is_accepted()
    {
        // The ordinary upload shape, and the one a naive implementation breaks: if the check counted the
        // tracker rather than the database, the document and its first version would refuse each other.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        using var db = Context(connection, tenantId);
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "bob.pem",
            MaskVersionId = await db.MaskVersions
                .Where(v => v.MaskId == WellKnownMaskIds.Certificate && v.IsCurrent)
                .Select(v => v.Id).FirstAsync(),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        db.DocumentVersions.Add(Version(document.Id, tenantId, userId));

        await db.SaveChangesAsync();   // no throw IS the assertion

        Assert.Equal(1, await db.DocumentVersions.CountAsync(v => v.DocumentId == document.Id));
    }

    [Fact]
    public async Task A_SECOND_version_of_a_certificate_is_refused_with_the_remedy_named()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        Guid documentId;
        using (var first = Context(connection, tenantId))
        {
            documentId = await FileAsync(first, tenantId, userId, WellKnownMaskIds.Certificate, "carol.pem");
            first.DocumentVersions.Add(Version(documentId, tenantId, userId));
            await first.SaveChangesAsync();
        }

        using var db = Context(connection, tenantId);
        db.DocumentVersions.Add(Version(documentId, tenantId, userId));

        var refusal = await Assert.ThrowsAsync<SingleVersionMaskException>(() => db.SaveChangesAsync());

        // The message must say what to do INSTEAD, or a policy refusal reads as a bug. It also has to name the
        // document: the upload paths catch InvalidOperationException wholesale and would otherwise report a
        // name clash on a name that is not the problem.
        Assert.Contains("carol.pem", refusal.Message);
        Assert.Contains("new document", refusal.Message);
    }

    [Fact]
    public async Task An_ordinary_document_takes_as_many_versions_as_it_likes()
    {
        // The complement, and worth pinning: this invariant runs on every save in the system, so a predicate
        // that leaked past its mask set would refuse version 2 of every document in the archive.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        Guid documentId;
        using (var first = Context(connection, tenantId))
        {
            documentId = await FileAsync(first, tenantId, userId, WellKnownMaskIds.BasicEntry, "report.pdf");
            first.DocumentVersions.Add(Version(documentId, tenantId, userId));
            await first.SaveChangesAsync();
        }

        using var db = Context(connection, tenantId);
        db.DocumentVersions.Add(Version(documentId, tenantId, userId));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.DocumentVersions.CountAsync(v => v.DocumentId == documentId));
    }
}
