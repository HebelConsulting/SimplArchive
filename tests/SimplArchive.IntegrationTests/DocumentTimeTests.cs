using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The optional UTC time-of-day on a document date (ADR "Optional time on the document date"): a nullable
// TimeOnly companion to DocumentDate. Pins that it persists both ways — a precise time and genuine absence —
// through the model both providers share.
public class DocumentTimeTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    private static (Tenant Tenant, User User, Document Doc) Seed(SimplArchiveDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "u@acme.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        var doc = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Doc", CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.Tenants.Add(tenant);
        db.Users.Add(user);
        db.Documents.Add(doc);
        return (tenant, user, doc);
    }

    private static DocumentVersion Version(Tenant tenant, User user, Document doc, TimeOnly? time) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant.Id,
        DocumentId = doc.Id,
        Status = DocumentVersionStatus.Confirmed,
        VersionNumber = 1,
        Sha256Hash = new string('0', 64),
        ObjectKey = "k",
        DocumentDate = new DateOnly(2026, 9, 6),
        DocumentTime = time,
        CreatedByUserId = user.Id,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task A_document_time_round_trips()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        Guid versionId;
        using (var db = CreateContext(connection, tenantAccessor))
        {
            var (tenant, user, doc) = Seed(db);
            tenantAccessor.TenantId = tenant.Id;
            var version = Version(tenant, user, doc, new TimeOnly(9, 50));
            versionId = version.Id;
            db.DocumentVersions.Add(version);
            await db.SaveChangesAsync();
        }

        using var read = CreateContext(connection, tenantAccessor);
        var loaded = await read.DocumentVersions.SingleAsync(v => v.Id == versionId);
        Assert.Equal(new TimeOnly(9, 50), loaded.DocumentTime);
    }

    [Fact]
    public async Task A_date_only_version_stores_a_null_time()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        Guid versionId;
        using (var db = CreateContext(connection, tenantAccessor))
        {
            var (tenant, user, doc) = Seed(db);
            tenantAccessor.TenantId = tenant.Id;
            var version = Version(tenant, user, doc, time: null);
            versionId = version.Id;
            db.DocumentVersions.Add(version);
            await db.SaveChangesAsync();
        }

        using var read = CreateContext(connection, tenantAccessor);
        var loaded = await read.DocumentVersions.SingleAsync(v => v.Id == versionId);
        Assert.Null(loaded.DocumentTime);
    }
}
