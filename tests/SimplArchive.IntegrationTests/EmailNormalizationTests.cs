using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class EmailNormalizationTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor());
    }

    [Fact]
    public void Setting_Email_populates_NormalizedEmail()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Email = "User@Example.com",
            DisplayName = "Test User",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("USER@EXAMPLE.COM", user.NormalizedEmail);
    }

    [Fact]
    public async Task Emails_differing_only_by_case_cannot_coexist_in_the_same_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Email = "user@example.com",
                DisplayName = "First",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var duplicateContext = CreateContext(connection);
        duplicateContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = "USER@EXAMPLE.COM",
            DisplayName = "Second",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
    }
}
