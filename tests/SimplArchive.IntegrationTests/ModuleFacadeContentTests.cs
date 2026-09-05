using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The content read (ABI 0.3, #1024): a module parses a document it owns — the syllabus-JSON case
// (flight-school ADR 0003). Resolves the current version, reads its object, gated by the same
// module-visibility consent as the field reads (that gate itself is proven in ModulePrincipalTests;
// here it is core-internal use, ungated).
public class ModuleFacadeContentTests
{
    private sealed class TestUserAccessor : ICurrentUserAccessor { public Guid? UserId { get; set; } }
    private sealed class TestServiceAccountAccessor : ICurrentServiceAccountAccessor { public Guid? ServiceAccountId { get; set; } }

    private static SimplArchiveDbContext Ctx(SqliteConnection c, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    [Fact]
    public async Task Reads_the_current_versions_content_bytes()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("""{"syllabus":"PPL(A)","revision":10}""");
        var storage = new InMemoryObjectStorage();
        storage.Objects["tenants/x/2026/abc/content.json"] = payload;

        var context = Ctx(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "s@x.io", DisplayName = "S", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = docId, TenantId = tenantId, Name = "Syllabus", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        context.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = docId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = "abc123",
            ObjectKey = "tenants/x/2026/abc/content.json",
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            DocumentDate = new DateOnly(2026, 1, 1),
        });
        await context.SaveChangesAsync();

        // Core-internal use (no module identity) — ungated, so the read resolves regardless of grants.
        var facade = new ModuleArchiveFacade(context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor(), objectStorage: storage);

        var content = await facade.GetDocumentContentAsync(docId);

        Assert.NotNull(content);
        Assert.Equal(payload, content);
    }

    [Fact]
    public async Task A_document_with_no_confirmed_version_reads_null()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var context = Ctx(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "s@x.io", DisplayName = "S", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = docId, TenantId = tenantId, Name = "Empty", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var facade = new ModuleArchiveFacade(context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor(), objectStorage: new InMemoryObjectStorage());

        Assert.Null(await facade.GetDocumentContentAsync(docId)); // no version → nothing to parse
    }
}
