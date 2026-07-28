using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class MaskNameUniquenessTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task Rejects_two_different_masks_current_versions_sharing_a_name_in_the_same_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var firstMaskId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Masks.Add(new Mask { Id = firstMaskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.MaskVersions.Add(new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = firstMaskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        var secondMaskId = Guid.NewGuid();
        using var context = CreateContext(connection, tenantId);
        context.Masks.Add(new Mask { Id = secondMaskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        context.MaskVersions.Add(new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = secondMaskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_the_same_mask_name_in_different_tenants()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var maskAId = Guid.NewGuid();
        var maskBId = Guid.NewGuid();

        using var context = CreateContext(connection);
        context.Tenants.AddRange(
            new Tenant { Id = tenantAId, Name = "Tenant A", CreatedAt = DateTimeOffset.UtcNow },
            new Tenant { Id = tenantBId, Name = "Tenant B", CreatedAt = DateTimeOffset.UtcNow });
        context.Masks.AddRange(
            new Mask { Id = maskAId, TenantId = tenantAId, CreatedAt = DateTimeOffset.UtcNow },
            new Mask { Id = maskBId, TenantId = tenantBId, CreatedAt = DateTimeOffset.UtcNow });
        context.MaskVersions.AddRange(
            new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantAId, MaskId = maskAId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow },
            new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantBId, MaskId = maskBId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(6, affected);
    }

    [Fact]
    public async Task Creating_a_new_version_increments_the_number_and_flips_the_old_version_off()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var firstVersionId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.MaskVersions.Add(new MaskVersion { Id = firstVersionId, TenantId = tenantId, MaskId = maskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        var secondVersionId = Guid.NewGuid();
        using (var context = CreateContext(connection, tenantId))
        {
            // Same Name as the first version is fine here — it's a version bump of the SAME mask,
            // not a collision with a different mask's current version.
            context.MaskVersions.Add(new MaskVersion { Id = secondVersionId, TenantId = tenantId, MaskId = maskId, Name = "Invoice", CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }

        using var readContext = CreateContext(connection, tenantId);
        var versions = await readContext.MaskVersions.Where(v => v.MaskId == maskId).ToListAsync();

        var firstVersion = versions.Single(v => v.Id == firstVersionId);
        var secondVersion = versions.Single(v => v.Id == secondVersionId);

        Assert.False(firstVersion.IsCurrent);
        Assert.Equal(1, firstVersion.VersionNumber);
        Assert.True(secondVersion.IsCurrent);
        Assert.Equal(2, secondVersion.VersionNumber);
    }
}
