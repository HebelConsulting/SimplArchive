using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class FieldValueFormatRangeTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private static async Task<(Guid TenantId, Guid MaskVersionId, Guid DocumentId)> SeedTenantAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        context.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        context.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Invoice #1", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        return (tenantId, maskVersionId, documentId);
    }

    [Fact]
    public async Task Rejects_a_text_value_not_matching_the_format_pattern()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedTenantAsync(connection);
        var fieldId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.FieldDefinitions.Add(new FieldDefinition
            {
                Id = fieldId,
                TenantId = tenantId,
                MaskVersionId = maskVersionId,
                Name = "InvoiceNumber",
                DataType = FieldDataType.Text,
                FormatPattern = "^INV-[0-9]{4}$",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = "not-an-invoice-number" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_text_value_exceeding_max_length()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedTenantAsync(connection);
        var fieldId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.FieldDefinitions.Add(new FieldDefinition
            {
                Id = fieldId,
                TenantId = tenantId,
                MaskVersionId = maskVersionId,
                Name = "ShortCode",
                DataType = FieldDataType.Text,
                MaxTextLength = 5,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = "TooLongValue" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_number_value_outside_the_configured_range()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedTenantAsync(connection);
        var fieldId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.FieldDefinitions.Add(new FieldDefinition
            {
                Id = fieldId,
                TenantId = tenantId,
                MaskVersionId = maskVersionId,
                Name = "Amount",
                DataType = FieldDataType.Number,
                MinValue = "0",
                MaxValue = "10000",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = "-5" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_date_value_outside_the_configured_range()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedTenantAsync(connection);
        var fieldId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.FieldDefinitions.Add(new FieldDefinition
            {
                Id = fieldId,
                TenantId = tenantId,
                MaskVersionId = maskVersionId,
                Name = "ValidFrom",
                DataType = FieldDataType.Date,
                MinValue = "2020-01-01T00:00:00Z",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = "2019-06-15T00:00:00Z" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_values_that_satisfy_format_and_range_constraints()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedTenantAsync(connection);
        var textFieldId = Guid.NewGuid();
        var numberFieldId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.FieldDefinitions.AddRange(
                new FieldDefinition { Id = textFieldId, TenantId = tenantId, MaskVersionId = maskVersionId, Name = "InvoiceNumber", DataType = FieldDataType.Text, FormatPattern = "^INV-[0-9]{4}$", CreatedAt = DateTimeOffset.UtcNow },
                new FieldDefinition { Id = numberFieldId, TenantId = tenantId, MaskVersionId = maskVersionId, Name = "Amount", DataType = FieldDataType.Number, MinValue = "0", MaxValue = "10000", CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.FieldValues.AddRange(
            new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = textFieldId, Value = "INV-0042" },
            new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = numberFieldId, Value = "500.50" });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(2, affected);
    }
}
