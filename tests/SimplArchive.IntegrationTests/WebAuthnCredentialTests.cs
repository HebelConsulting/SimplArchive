using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The WebAuthn passkey credential store (ADR "WebAuthn passkeys as a second factor"): tenant-scoped like every
// other principal-adjacent entity, unique on the raw CredentialId (a passkey is registered once globally), and
// cascade-deleted with its owning user.
public class WebAuthnCredentialTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor accessor) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, accessor);

    private static (Guid tenantId, Guid userId) SeedTenantUser(SimplArchiveDbContext context)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        context.Tenants.Add(new Tenant { Id = tenantId, Name = $"Tenant-{tenantId:N}", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@example.com", DisplayName = "Jane Doe", CreatedAt = DateTimeOffset.UtcNow });
        return (tenantId, userId);
    }

    private static WebAuthnCredential NewCredential(Guid tenantId, Guid userId, byte[] credentialId, string name = "My laptop") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = userId,
        CredentialId = credentialId,
        PublicKey = [4, 5, 6],
        SignCount = 0,
        AaGuid = Guid.NewGuid(),
        Name = name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Persists_and_reads_back_a_passkey_within_its_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection, new CurrentTenantAccessor()))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        Guid tenantId, userId;
        using (var seed = CreateContext(connection, new CurrentTenantAccessor()))
        {
            (tenantId, userId) = SeedTenantUser(seed);
            seed.WebAuthnCredentials.Add(NewCredential(tenantId, userId, [1, 2, 3]));
            await seed.SaveChangesAsync();
        }

        using var read = CreateContext(connection, new CurrentTenantAccessor { TenantId = tenantId });
        var credential = await read.WebAuthnCredentials.SingleAsync();
        Assert.Equal(userId, credential.UserId);
        Assert.Equal("My laptop", credential.Name);
        Assert.Equal([1, 2, 3], credential.CredentialId);
    }

    [Fact]
    public async Task Tenant_query_filter_isolates_passkeys()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection, new CurrentTenantAccessor()))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        Guid tenantA, userA, tenantB, userB;
        using (var seed = CreateContext(connection, new CurrentTenantAccessor()))
        {
            (tenantA, userA) = SeedTenantUser(seed);
            (tenantB, userB) = SeedTenantUser(seed);
            seed.WebAuthnCredentials.Add(NewCredential(tenantA, userA, [1, 1, 1]));
            seed.WebAuthnCredentials.Add(NewCredential(tenantB, userB, [2, 2, 2]));
            await seed.SaveChangesAsync();
        }

        using var scoped = CreateContext(connection, new CurrentTenantAccessor { TenantId = tenantA });
        var visible = await scoped.WebAuthnCredentials.ToListAsync();
        Assert.Equal(userA, Assert.Single(visible).UserId);
    }

    [Fact]
    public async Task Rejects_a_duplicate_credential_id()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection, new CurrentTenantAccessor()))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        using var context = CreateContext(connection, new CurrentTenantAccessor());
        var (tenantId, userId) = SeedTenantUser(context);
        context.WebAuthnCredentials.Add(NewCredential(tenantId, userId, [9, 9, 9], "First"));
        await context.SaveChangesAsync();

        context.WebAuthnCredentials.Add(NewCredential(tenantId, userId, [9, 9, 9], "Second"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_user_cascades_to_its_passkeys()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection, new CurrentTenantAccessor()))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        Guid tenantId, userId;
        using (var seed = CreateContext(connection, new CurrentTenantAccessor()))
        {
            (tenantId, userId) = SeedTenantUser(seed);
            seed.WebAuthnCredentials.Add(NewCredential(tenantId, userId, [7, 7, 7]));
            await seed.SaveChangesAsync();
        }

        using (var delete = CreateContext(connection, new CurrentTenantAccessor { TenantId = tenantId }))
        {
            delete.Users.Remove(await delete.Users.SingleAsync(u => u.Id == userId));
            await delete.SaveChangesAsync();
        }

        using var verify = CreateContext(connection, new CurrentTenantAccessor { TenantId = tenantId });
        Assert.Empty(await verify.WebAuthnCredentials.ToListAsync());
    }
}
