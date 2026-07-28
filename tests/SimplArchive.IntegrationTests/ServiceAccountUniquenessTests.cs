using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class ServiceAccountUniquenessTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task Rejects_two_service_accounts_with_the_same_name_in_the_same_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "CI Uploader",
                OpenIddictApplicationClientId = "svc-ci-uploader",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.ServiceAccounts.Add(new ServiceAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "CI Uploader",
            OpenIddictApplicationClientId = "svc-ci-uploader-2",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_two_service_accounts_with_the_same_openiddict_client_id_in_the_same_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "CI Uploader",
                OpenIddictApplicationClientId = "svc-ci-uploader",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.ServiceAccounts.Add(new ServiceAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Nightly Export",
            OpenIddictApplicationClientId = "svc-ci-uploader",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_the_same_name_and_client_id_in_different_tenants()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "CI Uploader",
                OpenIddictApplicationClientId = "svc-ci-uploader",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, otherTenantId);
        context.ServiceAccounts.Add(new ServiceAccount
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenantId,
            Name = "CI Uploader",
            OpenIddictApplicationClientId = "svc-ci-uploader",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(1, affected);
    }
}
