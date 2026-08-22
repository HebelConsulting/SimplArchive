using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A personal space carries its owner's name, and keeps carrying it (ADR 0671, #702).
//
// The rename rides in the same SaveChanges as the display-name change, so there is no window in which a person
// and their space disagree about who they are — and no call site that can forget.
public class PersonalRootFollowsDisplayNameTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<(Guid TenantId, Guid UserId, Guid RootId)> SeedAsync(SqliteConnection connection, string displayName)
    {
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var (tenantId, userId, rootId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        using var db = CreateContext(connection);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = now });
        db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "anna.schmidt@example.com", DisplayName = displayName, CreatedAt = now });
        db.Documents.Add(new Document
        {
            Id = rootId,
            TenantId = tenantId,
            Name = displayName,
            PersonalOfUserId = userId,
            CreatedByUserId = userId,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return (tenantId, userId, rootId);
    }

    private static async Task<string> RootNameAsync(SqliteConnection connection, Guid tenantId, Guid rootId)
    {
        using var db = CreateContext(connection, tenantId);
        return (await db.Documents.SingleAsync(d => d.Id == rootId)).Name;
    }

    [Fact]
    public async Task Renaming_the_person_renames_their_space()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId, rootId) = await SeedAsync(connection, "Anna Schmidt");

        using (var db = CreateContext(connection, tenantId))
        {
            (await db.Users.SingleAsync(u => u.Id == userId)).DisplayName = "Anna Meier";
            await db.SaveChangesAsync();
        }

        Assert.Equal("Anna Meier", await RootNameAsync(connection, tenantId, rootId));
    }

    [Fact]
    public async Task The_new_name_is_sanitised_on_the_way_in()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId, rootId) = await SeedAsync(connection, "Anna Schmidt");

        using (var db = CreateContext(connection, tenantId))
        {
            (await db.Users.SingleAsync(u => u.Id == userId)).DisplayName = "Anna / Tom ";
            await db.SaveChangesAsync();
        }

        // A separator reaching the name would split one space into two WebDAV path segments, addressing
        // something that does not exist — which is why sanitation happens here rather than in the path builder.
        Assert.Equal("Anna - Tom", await RootNameAsync(connection, tenantId, rootId));
    }

    [Fact]
    public async Task A_space_still_carrying_the_old_name_is_not_renamed_by_an_unrelated_change()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId, rootId) = await SeedAsync(connection, "Anna Schmidt");

        // The pre-0671 state, which is NOT migrated: a space provisioned when every one of them was called
        // "Personal". Such a root exists for as long as its owner is never renamed.
        using (var db = CreateContext(connection, tenantId))
        {
            (await db.Documents.SingleAsync(d => d.Id == rootId)).Name = "Personal";
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, tenantId))
        {
            (await db.Users.SingleAsync(u => u.Id == userId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        // Deactivating somebody must not move their mounted drive. The hook fires on a DISPLAY-NAME change, not
        // on any change to the person — without that guard, an unrelated save would quietly "correct" a legacy
        // name and break a saved favourite at a moment nothing explains.
        Assert.Equal("Personal", await RootNameAsync(connection, tenantId, rootId));
    }

    [Fact]
    public async Task A_user_with_no_personal_space_yet_saves_normally()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var (tenantId, userId) = (Guid.NewGuid(), Guid.NewGuid());
        using (var db = CreateContext(connection))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "tom@example.com", DisplayName = "Tom", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Most users are renamed before they ever open the personal tab, so "no root yet" is the common path,
        // not an edge case — and it must not throw on the way through a hook that has nothing to do.
        using (var db = CreateContext(connection, tenantId))
        {
            (await db.Users.SingleAsync(u => u.Id == userId)).DisplayName = "Tom Baker";
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, tenantId))
        {
            Assert.Equal("Tom Baker", (await db.Users.SingleAsync(u => u.Id == userId)).DisplayName);
        }
    }
}
