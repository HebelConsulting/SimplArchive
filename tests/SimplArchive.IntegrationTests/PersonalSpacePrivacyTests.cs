using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A personal space is private, and access without a grant is a right (ADR 0670, #702).
//
// The two halves are tested together because they only make sense together: the tenant-admin bypass stops
// applying inside somebody else's personal space, and CanAccessWithoutGrant is what an administrator keeps
// there. Test either alone and you can convince yourself of a system nobody would ship — a privacy rule with no
// way in, or a right that changes nothing.
public class PersonalSpacePrivacyTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    // One tenant, an owner with a personal space holding one document, an admin, and an ordinary repository.
    private sealed record World(
        Guid TenantId, Guid OwnerId, Guid AdminId, Guid PersonalRootId, Guid PersonalDocId, Guid SharedDocId);

    private static async Task<World> SeedAsync(
        SqliteConnection connection,
        bool adminHoldsRight = false,
        bool adminActive = true,
        Action<SimplArchiveDbContext, World>? extra = null)
    {
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var w = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        using var db = CreateContext(connection);
        db.Tenants.Add(new Tenant { Id = w.TenantId, Name = "T", CreatedAt = now });
        db.Users.Add(new User { Id = w.OwnerId, TenantId = w.TenantId, Email = "owner@example.com", DisplayName = "Owner", CreatedAt = now });
        db.Users.Add(new User
        {
            Id = w.AdminId,
            TenantId = w.TenantId,
            Email = "admin@example.com",
            DisplayName = "Admin",
            IsActive = adminActive,
            IsTenantAdmin = true,
            CanAccessWithoutGrant = adminHoldsRight,
            CreatedAt = now,
        });

        // The personal space: a root flagged with its owner, and one document inside it.
        db.Documents.Add(new Document { Id = w.PersonalRootId, TenantId = w.TenantId, Name = "Owner", PersonalOfUserId = w.OwnerId, CreatedByUserId = w.OwnerId, CreatedAt = now });
        db.Documents.Add(new Document { Id = w.PersonalDocId, TenantId = w.TenantId, Name = "Private note", ParentId = w.PersonalRootId, CreatedByUserId = w.OwnerId, CreatedAt = now });

        // ...and an ordinary repository, which is the control: whatever changes inside the personal space must
        // demonstrably NOT change out here, or the test is measuring a broken bypass rather than a narrowed one.
        db.Documents.Add(new Document { Id = w.SharedDocId, TenantId = w.TenantId, Name = "Shared", CreatedByUserId = w.AdminId, CreatedAt = now });

        db.AclEntries.Add(new AclEntry
        {
            Id = Guid.NewGuid(),
            TenantId = w.TenantId,
            DocumentId = w.PersonalRootId,
            UserId = w.OwnerId,
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanDelete = true,
            CreatedAt = now,
        });

        extra?.Invoke(db, w);
        await db.SaveChangesAsync();
        return w;
    }

    private static EffectiveRightsCalculator CalculatorFor(SqliteConnection connection, Guid tenantId) =>
        new(CreateContext(connection, tenantId));

    [Fact]
    public async Task An_admin_without_the_right_gets_nothing_inside_another_users_personal_space()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection);

        var rights = await CalculatorFor(connection, w.TenantId).GetEffectiveRightsAsync(w.AdminId, w.PersonalDocId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanReadContent);
    }

    [Fact]
    public async Task The_right_gives_see_and_read_there_and_nothing_else()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection, adminHoldsRight: true);

        var rights = await CalculatorFor(connection, w.TenantId).GetEffectiveRightsAsync(w.AdminId, w.PersonalDocId);

        Assert.True(rights.CanSee);
        Assert.True(rights.CanReadContent);

        // The whole point of naming it "access", not "manage": everything else stays false, so an administrator
        // can read a private space without being able to alter, move or re-permission anything in it.
        Assert.False(rights.CanEditContent);
        Assert.False(rights.CanEditIndexData);
        Assert.False(rights.CanDelete);
        Assert.False(rights.CanCreateSubItems);
        Assert.False(rights.CanManagePermissions);
        Assert.False(rights.CanMove);
        Assert.False(rights.CanAnnotate);
    }

    [Fact]
    public async Task Outside_a_personal_space_the_admin_bypass_is_untouched()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection);

        var rights = await CalculatorFor(connection, w.TenantId).GetEffectiveRightsAsync(w.AdminId, w.SharedDocId);

        Assert.True(rights.CanSee);
        Assert.True(rights.CanEditContent);
        Assert.True(rights.CanManagePermissions);
    }

    [Fact]
    public async Task The_owner_still_holds_their_own_space()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection);

        var rights = await CalculatorFor(connection, w.TenantId).GetEffectiveRightsAsync(w.OwnerId, w.PersonalDocId);

        Assert.True(rights.CanSee);
        Assert.True(rights.CanEditContent);
    }

    [Fact]
    public async Task A_deactivated_holder_gets_nothing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection, adminHoldsRight: true, adminActive: false);

        // The ordering ADRs 0174/0153 fixed: the active check runs BEFORE any bypass and before this right. A
        // right that could resurrect a deactivated account would be a worse hole than the one being closed.
        var rights = await CalculatorFor(connection, w.TenantId).GetEffectiveRightsAsync(w.AdminId, w.SharedDocId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanReadContent);
    }

    [Fact]
    public async Task The_right_does_not_top_up_a_real_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        // A plain user, holding the right, who has ALSO been granted CanSee alone on the shared document.
        var readerId = Guid.NewGuid();
        var w = await SeedAsync(connection, extra: (db, w) =>
        {
            db.Users.Add(new User
            {
                Id = readerId,
                TenantId = w.TenantId,
                Email = "reader@example.com",
                DisplayName = "Reader",
                CanAccessWithoutGrant = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.AclEntries.Add(new AclEntry
            {
                Id = Guid.NewGuid(),
                TenantId = w.TenantId,
                DocumentId = w.SharedDocId,
                UserId = readerId,
                CanSee = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        });

        var rights = await CalculatorFor(connection, w.TenantId).GetEffectiveRightsAsync(readerId, w.SharedDocId);

        // Somebody decided this person may SEE but not READ. The right is conditioned on lacking CanSee
        // precisely so a blanket permission cannot quietly widen a deliberate grant.
        Assert.True(rights.CanSee);
        Assert.False(rights.CanReadContent);
    }

    [Fact]
    public async Task A_group_conferred_admin_is_narrowed_the_same_way()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var memberId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var w = await SeedAsync(connection, extra: (db, w) =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(new User { Id = memberId, TenantId = w.TenantId, Email = "m@example.com", DisplayName = "M", CreatedAt = now });
            db.Groups.Add(new Group { Id = groupId, TenantId = w.TenantId, Name = "Admins", IsTenantAdmin = true, CreatedAt = now });
            db.GroupMemberships.Add(new GroupMembership { TenantId = w.TenantId, GroupId = groupId, UserId = memberId });
        });

        var calculator = CalculatorFor(connection, w.TenantId);

        // Admin everywhere else...
        Assert.True((await calculator.GetEffectiveRightsAsync(memberId, w.SharedDocId)).CanEditContent);

        // ...and an ordinary caller inside somebody's personal space. A group-conferred admin is no more
        // entitled to read a private space than a directly-flagged one, and the narrowing is applied at a
        // different place in the method for each, which is exactly why both are asserted.
        Assert.False((await calculator.GetEffectiveRightsAsync(memberId, w.PersonalDocId)).CanSee);
    }

    [Fact]
    public async Task Clearance_still_blocks_a_holder_of_the_right()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var auditorId = Guid.NewGuid();
        var labelId = Guid.NewGuid();
        var w = await SeedAsync(connection, extra: (db, w) =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(new User
            {
                Id = auditorId,
                TenantId = w.TenantId,
                Email = "auditor@example.com",
                DisplayName = "Auditor",
                CanAccessWithoutGrant = true,
                ClearanceRank = 0,
                CreatedAt = now,
            });
            db.SensitivityLabelDefinitions.Add(new SensitivityLabelDefinition { Id = labelId, TenantId = w.TenantId, Name = "Secret", Rank = 5, CreatedAt = now });
        });

        using (var db = CreateContext(connection, w.TenantId))
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == w.TenantId);
            tenant.EnforceClearance = true;
            var doc = await db.Documents.SingleAsync(d => d.Id == w.SharedDocId);
            doc.SensitivityLabelId = labelId;
            await db.SaveChangesAsync();
        }

        var rights = await CalculatorFor(connection, w.TenantId).GetEffectiveRightsAsync(auditorId, w.SharedDocId);

        // "Reads globally" is about grants, not about clearance. A non-admin holder is still a non-admin.
        Assert.False(rights.CanSee);
    }

    [Fact]
    public async Task The_column_follows_a_document_into_and_out_of_a_personal_space()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection);

        // Derived on insert, from a parent inserted in the SAME SaveChanges — the ordering case that would
        // otherwise file an entire freshly-provisioned personal space as "not personal".
        using (var db = CreateContext(connection, w.TenantId))
        {
            Assert.Equal(w.OwnerId, (await db.Documents.SingleAsync(d => d.Id == w.PersonalDocId)).PersonalRootOwnerId);
            Assert.Equal(w.OwnerId, (await db.Documents.SingleAsync(d => d.Id == w.PersonalRootId)).PersonalRootOwnerId);
            Assert.Null((await db.Documents.SingleAsync(d => d.Id == w.SharedDocId)).PersonalRootOwnerId);
        }

        // A grandchild, so the move below has a subtree to rewrite rather than a single row.
        var grandchildId = Guid.NewGuid();
        using (var db = CreateContext(connection, w.TenantId))
        {
            db.Documents.Add(new Document { Id = grandchildId, TenantId = w.TenantId, Name = "Deeper", ParentId = w.PersonalDocId, CreatedByUserId = w.OwnerId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Moving the middle document out must take its descendants with it — the half a per-row rule would miss.
        using (var db = CreateContext(connection, w.TenantId))
        {
            (await db.Documents.SingleAsync(d => d.Id == w.PersonalDocId)).ParentId = w.SharedDocId;
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, w.TenantId))
        {
            Assert.Null((await db.Documents.SingleAsync(d => d.Id == w.PersonalDocId)).PersonalRootOwnerId);
            Assert.Null((await db.Documents.SingleAsync(d => d.Id == grandchildId)).PersonalRootOwnerId);
        }

        // ...and back in again, which is the direction that actually grants privacy rather than removing it.
        using (var db = CreateContext(connection, w.TenantId))
        {
            (await db.Documents.SingleAsync(d => d.Id == w.PersonalDocId)).ParentId = w.PersonalRootId;
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, w.TenantId))
        {
            Assert.Equal(w.OwnerId, (await db.Documents.SingleAsync(d => d.Id == grandchildId)).PersonalRootOwnerId);
        }
    }
}
