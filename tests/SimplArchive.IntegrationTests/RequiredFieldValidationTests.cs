using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class RequiredFieldValidationTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private record Fixture(Guid TenantId, Guid UserId, Guid MaskVersionId, Guid RequiredFieldId);

    private static async Task<Fixture> SeedMaskWithRequiredFieldAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();
        var requiredFieldId = Guid.NewGuid();

        using var seedContext = CreateContext(connection);
        seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        seedContext.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        seedContext.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });
        seedContext.FieldDefinitions.Add(new FieldDefinition
        {
            Id = requiredFieldId,
            TenantId = tenantId,
            MaskVersionId = maskVersionId,
            Name = "InvoiceNumber",
            DataType = FieldDataType.Text,
            IsRequired = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await seedContext.SaveChangesAsync();

        return new Fixture(tenantId, userId, maskVersionId, requiredFieldId);
    }

    [Fact]
    public async Task Rejects_assigning_a_mask_to_a_document_missing_a_required_field_value()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedMaskWithRequiredFieldAsync(connection);

        using var context = CreateContext(connection, fixture.TenantId);
        context.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = fixture.TenantId,
            Name = "Invoice #1",
            MaskVersionId = fixture.MaskVersionId,
            CreatedByUserId = fixture.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_assigning_a_mask_when_the_required_field_value_is_supplied_in_the_same_batch()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedMaskWithRequiredFieldAsync(connection);
        var documentId = Guid.NewGuid();

        using var context = CreateContext(connection, fixture.TenantId);
        context.Documents.Add(new Document
        {
            Id = documentId,
            TenantId = fixture.TenantId,
            Name = "Invoice #1",
            MaskVersionId = fixture.MaskVersionId,
            CreatedByUserId = fixture.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        context.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = fixture.TenantId,
            DocumentId = documentId,
            FieldDefinitionId = fixture.RequiredFieldId,
            Value = "INV-0001",
        });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(2, affected);
    }

    [Fact]
    public async Task Allows_creating_a_document_without_a_mask_and_filling_in_fields_later()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedMaskWithRequiredFieldAsync(connection);
        var documentId = Guid.NewGuid();

        using (var createContext = CreateContext(connection, fixture.TenantId))
        {
            createContext.Documents.Add(new Document
            {
                Id = documentId,
                TenantId = fixture.TenantId,
                Name = "Invoice #1",
                CreatedByUserId = fixture.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            var affected = await createContext.SaveChangesAsync();
            Assert.Equal(1, affected);
        }

        using (var maskContext = CreateContext(connection, fixture.TenantId))
        {
            var document = await maskContext.Documents.SingleAsync(d => d.Id == documentId);
            document.MaskVersionId = fixture.MaskVersionId;

            await Assert.ThrowsAsync<InvalidOperationException>(() => maskContext.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Allows_editing_other_field_values_after_the_mask_is_already_assigned_and_complete()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedMaskWithRequiredFieldAsync(connection);
        var documentId = Guid.NewGuid();
        var requiredFieldValueId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection, fixture.TenantId))
        {
            seedContext.Documents.Add(new Document
            {
                Id = documentId,
                TenantId = fixture.TenantId,
                Name = "Invoice #1",
                MaskVersionId = fixture.MaskVersionId,
                CreatedByUserId = fixture.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            seedContext.FieldValues.Add(new FieldValue
            {
                Id = requiredFieldValueId,
                TenantId = fixture.TenantId,
                DocumentId = documentId,
                FieldDefinitionId = fixture.RequiredFieldId,
                Value = "INV-0001",
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, fixture.TenantId);
        var requiredFieldValue = await context.FieldValues.SingleAsync(v => v.Id == requiredFieldValueId);
        requiredFieldValue.Value = "INV-0002";

        var affected = await context.SaveChangesAsync();

        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task Allows_clearing_a_documents_mask_assignment_with_no_required_field_check()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedMaskWithRequiredFieldAsync(connection);
        var documentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection, fixture.TenantId))
        {
            seedContext.Documents.Add(new Document
            {
                Id = documentId,
                TenantId = fixture.TenantId,
                Name = "Invoice #1",
                MaskVersionId = fixture.MaskVersionId,
                CreatedByUserId = fixture.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            seedContext.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = fixture.TenantId,
                DocumentId = documentId,
                FieldDefinitionId = fixture.RequiredFieldId,
                Value = "INV-0001",
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, fixture.TenantId);
        var document = await context.Documents.SingleAsync(d => d.Id == documentId);
        document.MaskVersionId = null;

        var affected = await context.SaveChangesAsync();

        Assert.Equal(1, affected);
    }
}
