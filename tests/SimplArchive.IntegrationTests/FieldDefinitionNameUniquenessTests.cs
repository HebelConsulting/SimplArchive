using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class FieldDefinitionNameUniquenessTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task Rejects_two_fields_with_the_same_name_on_the_same_mask_version()
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

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.FieldDefinitions.Add(new FieldDefinition { Id = Guid.NewGuid(), TenantId = tenantId, MaskVersionId = maskVersionId, Name = "Amount", DataType = FieldDataType.Number, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.FieldDefinitions.Add(new FieldDefinition { Id = Guid.NewGuid(), TenantId = tenantId, MaskVersionId = maskVersionId, Name = "Amount", DataType = FieldDataType.Number, CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_the_same_field_name_on_different_masks()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var invoiceMaskId = Guid.NewGuid();
        var invoiceMaskVersionId = Guid.NewGuid();
        var contractMaskId = Guid.NewGuid();
        var contractMaskVersionId = Guid.NewGuid();

        using var context = CreateContext(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Masks.AddRange(
            new Mask { Id = invoiceMaskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow },
            new Mask { Id = contractMaskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        context.MaskVersions.AddRange(
            new MaskVersion { Id = invoiceMaskVersionId, TenantId = tenantId, MaskId = invoiceMaskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow },
            new MaskVersion { Id = contractMaskVersionId, TenantId = tenantId, MaskId = contractMaskId, Name = "Contract", CreatedAt = DateTimeOffset.UtcNow });
        context.FieldDefinitions.AddRange(
            new FieldDefinition { Id = Guid.NewGuid(), TenantId = tenantId, MaskVersionId = invoiceMaskVersionId, Name = "Amount", DataType = FieldDataType.Number, CreatedAt = DateTimeOffset.UtcNow },
            new FieldDefinition { Id = Guid.NewGuid(), TenantId = tenantId, MaskVersionId = contractMaskVersionId, Name = "Amount", DataType = FieldDataType.Number, CreatedAt = DateTimeOffset.UtcNow });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(7, affected);
    }
}
