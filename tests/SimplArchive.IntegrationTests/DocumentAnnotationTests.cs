using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// DocumentAnnotation (ADR "Document annotations (sticky notes)"): a positional note pinned to a document
// version. Exercises persistence, tenant isolation, the exactly-one-creator + position CHECK constraints,
// and the document-delete cascade — live against SQLite.
public class DocumentAnnotationTests
{
    private static SimplArchiveDbContext Ctx(SqliteConnection c, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, new CurrentTenantAccessor { TenantId = tenantId });

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();
    private readonly Guid _versionId = Guid.NewGuid();

    private async Task SeedDocumentAsync(SqliteConnection connection)
    {
        using var db = Ctx(connection);
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new User { Id = _userId, TenantId = _tenantId, Email = "u@acme.test", DisplayName = "User", CreatedAt = DateTimeOffset.UtcNow });
        db.Documents.Add(new Document { Id = _documentId, TenantId = _tenantId, Name = "Doc", CreatedByUserId = _userId, CreatedAt = DateTimeOffset.UtcNow });
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = _versionId,
            TenantId = _tenantId,
            DocumentId = _documentId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = new string('a', 64),
            ObjectKey = "tenants/x/2026/v.pdf",
            CreatedByUserId = _userId,
            CreatedAt = DateTimeOffset.UtcNow,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        await db.SaveChangesAsync();
    }

    private DocumentAnnotation NewNote() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        DocumentId = _documentId,
        DocumentVersionId = _versionId,
        PageIndex = 0,
        PositionX = 0.25,
        PositionY = 0.5,
        Text = "Check this figure",
        Color = "#FFEB3B",
        CreatedByUserId = _userId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Persists_and_reads_back_a_note()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedDocumentAsync(connection);

        using (var db = Ctx(connection, _tenantId))
        {
            db.DocumentAnnotations.Add(NewNote());
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, _tenantId))
        {
            var note = await db.DocumentAnnotations.SingleAsync();
            Assert.Equal(_versionId, note.DocumentVersionId);
            Assert.Equal(0.25, note.PositionX);
            Assert.Equal("Check this figure", note.Text);
            Assert.Equal("#FFEB3B", note.Color);
            Assert.NotEqual(Guid.Empty, note.ConcurrencyToken); // auto-assigned (IConcurrencyTracked)
        }
    }

    [Fact]
    public async Task Tenant_filter_scopes_notes()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedDocumentAsync(connection);

        using (var db = Ctx(connection, _tenantId))
        {
            db.DocumentAnnotations.Add(NewNote());
            await db.SaveChangesAsync();
        }

        // A different tenant's context sees none.
        using (var other = Ctx(connection, Guid.NewGuid()))
        {
            Assert.Empty(await other.DocumentAnnotations.ToListAsync());
        }
    }

    [Fact]
    public async Task Rejects_zero_or_two_creators()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedDocumentAsync(connection);
        var serviceAccountId = Guid.NewGuid();
        using (var db = Ctx(connection, _tenantId))
        {
            db.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = _tenantId, Name = "SA", OpenIddictApplicationClientId = "sa", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, _tenantId))
        {
            var none = NewNote();
            none.CreatedByUserId = null;
            db.DocumentAnnotations.Add(none);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (var db = Ctx(connection, _tenantId))
        {
            var both = NewNote();
            both.CreatedByServiceAccountId = serviceAccountId;
            db.DocumentAnnotations.Add(both);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Rejects_an_out_of_range_position()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedDocumentAsync(connection);

        using var db = Ctx(connection, _tenantId);
        var note = NewNote();
        note.PositionX = 1.5;
        db.DocumentAnnotations.Add(note);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_the_document_cascades_to_its_notes()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedDocumentAsync(connection);

        using (var db = Ctx(connection, _tenantId))
        {
            db.DocumentAnnotations.Add(NewNote());
            await db.SaveChangesAsync();
        }

        // Hard-delete the document (bypass the soft-delete filter to load it) — the cascade removes its notes.
        using (var db = Ctx(connection, _tenantId))
        {
            var doc = await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).SingleAsync(d => d.Id == _documentId);
            db.Documents.Remove(doc);
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, _tenantId))
        {
            Assert.Empty(await db.DocumentAnnotations.IgnoreQueryFilters().ToListAsync());
        }
    }
}
