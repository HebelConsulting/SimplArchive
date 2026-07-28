using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// RepositoryMask (mask-to-repository assignment) was removed entirely — masks are now tenant-wide, usable
// by any Document in the tenant directly, no explicit per-repository opt-in step — see ADR
// "Repository/Document unification".
public class MaskAndFieldValueTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task A_multi_select_fields_several_values_become_separate_rows_sharing_the_same_document_and_field()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.FieldDefinitions.Add(new FieldDefinition
            {
                Id = fieldId,
                TenantId = tenantId,
                MaskVersionId = maskVersionId,
                Name = "Tags",
                DataType = FieldDataType.MultiSelect,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Invoice #1", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.FieldValues.AddRange(
            new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = "Urgent" },
            new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = "Reviewed" });

        await context.SaveChangesAsync();

        var values = await context.FieldValues
            .Where(v => v.DocumentId == documentId && v.FieldDefinitionId == fieldId)
            .Select(v => v.Value)
            .ToListAsync();

        Assert.Equal(["Reviewed", "Urgent"], values.OrderBy(v => v));
    }
}
