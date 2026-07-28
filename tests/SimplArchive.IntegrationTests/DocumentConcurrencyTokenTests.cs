using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class DocumentConcurrencyTokenTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private record Fixture(Guid TenantId, Guid DocumentId);

    private static async Task<Fixture> SeedDocumentAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using var seedContext = CreateContext(connection);
        seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Invoice.pdf", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await seedContext.SaveChangesAsync();

        return new Fixture(tenantId, documentId);
    }

    [Fact]
    public async Task A_new_document_gets_a_non_empty_concurrency_token()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedDocumentAsync(connection);

        using var context = CreateContext(connection, fixture.TenantId);
        var document = await context.Documents.SingleAsync(d => d.Id == fixture.DocumentId);

        Assert.NotEqual(Guid.Empty, document.ConcurrencyToken);
    }

    [Fact]
    public async Task Updating_a_document_changes_its_concurrency_token()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedDocumentAsync(connection);

        Guid originalToken;
        using (var readContext = CreateContext(connection, fixture.TenantId))
        {
            originalToken = (await readContext.Documents.SingleAsync(d => d.Id == fixture.DocumentId)).ConcurrencyToken;
        }

        using (var updateContext = CreateContext(connection, fixture.TenantId))
        {
            var document = await updateContext.Documents.SingleAsync(d => d.Id == fixture.DocumentId);
            document.Name = "Renamed.pdf";
            await updateContext.SaveChangesAsync();
        }

        using var verifyContext = CreateContext(connection, fixture.TenantId);
        var updated = await verifyContext.Documents.SingleAsync(d => d.Id == fixture.DocumentId);

        Assert.NotEqual(originalToken, updated.ConcurrencyToken);
    }

    [Fact]
    public async Task Rejects_an_update_using_a_stale_concurrency_token()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedDocumentAsync(connection);

        Guid staleToken;
        using (var readContext = CreateContext(connection, fixture.TenantId))
        {
            staleToken = (await readContext.Documents.SingleAsync(d => d.Id == fixture.DocumentId)).ConcurrencyToken;
        }

        // A concurrent writer updates the document first, advancing its ConcurrencyToken.
        using (var firstWriterContext = CreateContext(connection, fixture.TenantId))
        {
            var document = await firstWriterContext.Documents.SingleAsync(d => d.Id == fixture.DocumentId);
            document.Name = "First writer's rename.pdf";
            await firstWriterContext.SaveChangesAsync();
        }

        // A second writer, still holding the now-stale token, attempts its own update.
        using var secondWriterContext = CreateContext(connection, fixture.TenantId);
        var staleDocument = await secondWriterContext.Documents.SingleAsync(d => d.Id == fixture.DocumentId);
        staleDocument.Name = "Second writer's rename.pdf";
        secondWriterContext.Entry(staleDocument).Property(d => d.ConcurrencyToken).OriginalValue = staleToken;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondWriterContext.SaveChangesAsync());
    }
}
