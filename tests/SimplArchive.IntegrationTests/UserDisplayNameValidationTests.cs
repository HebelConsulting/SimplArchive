using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class UserDisplayNameValidationTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_or_space_only_display_name(string blankName)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = "a@example.com", DisplayName = blankName, CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_a_non_blank_display_name()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = "a@example.com", DisplayName = "Jane Doe", CreatedAt = DateTimeOffset.UtcNow });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(2, affected); // Tenant + User
    }
}
