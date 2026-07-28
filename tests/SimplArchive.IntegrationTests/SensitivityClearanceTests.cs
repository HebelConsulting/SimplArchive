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

// ADR "Sensitivity clearance enforcement": when the tenant enforces clearance, EffectiveRightsCalculator returns
// NoRights for a document whose sensitivity-label Rank exceeds the caller's effective clearance (own ⊔ groups) —
// "no CanSee". Off by default; a tenant admin bypasses; a group confers clearance to members.
public class SensitivityClearanceTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record World(Guid TenantId, Guid UserId, Guid AdminId, Guid GroupId, Guid ConfidentialDocId, Guid PublicDocId, Guid UnlabelledDocId, Guid ConfidentialLabelId, Guid PublicLabelId);

    private static async Task<World> SeedAsync(SqliteConnection connection, bool enforce, int userClearance, int groupClearance = 0)
    {
        var w = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var ctx = CreateContext(connection);
        ctx.Tenants.Add(new Tenant { Id = w.TenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow, EnforceClearance = enforce });
        ctx.Users.Add(new User { Id = w.UserId, TenantId = w.TenantId, Email = "u@x.com", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow, ClearanceRank = userClearance });
        ctx.Users.Add(new User { Id = w.AdminId, TenantId = w.TenantId, Email = "a@x.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow, IsTenantAdmin = true, ClearanceRank = 0 });
        ctx.Groups.Add(new Group { Id = w.GroupId, TenantId = w.TenantId, Name = "G", CreatedAt = DateTimeOffset.UtcNow, ClearanceRank = groupClearance });

        ctx.SensitivityLabelDefinitions.Add(new SensitivityLabelDefinition { Id = w.PublicLabelId, TenantId = w.TenantId, Name = "Public", Rank = 1, CreatedAt = DateTimeOffset.UtcNow });
        ctx.SensitivityLabelDefinitions.Add(new SensitivityLabelDefinition { Id = w.ConfidentialLabelId, TenantId = w.TenantId, Name = "Confidential", Rank = 3, CreatedAt = DateTimeOffset.UtcNow });

        // A root document, three leaves under it (all directly granted CanSee to the user + admin so ACL never
        // hides them — only clearance can).
        var rootId = Guid.NewGuid();
        ctx.Documents.Add(new Document { Id = rootId, TenantId = w.TenantId, Name = "Root", CreatedByUserId = w.AdminId, CreatedAt = DateTimeOffset.UtcNow, BreaksInheritance = true });
        foreach (var (id, labelId, name) in new[] { (w.ConfidentialDocId, (Guid?)w.ConfidentialLabelId, "conf"), (w.PublicDocId, w.PublicLabelId, "pub"), (w.UnlabelledDocId, (Guid?)null, "none") })
        {
            ctx.Documents.Add(new Document { Id = id, TenantId = w.TenantId, ParentId = rootId, Name = name, CreatedByUserId = w.AdminId, CreatedAt = DateTimeOffset.UtcNow, BreaksInheritance = true, SensitivityLabelId = labelId });
            ctx.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = w.TenantId, DocumentId = id, UserId = w.UserId, CanSee = true, CanReadContent = true, CreatedAt = DateTimeOffset.UtcNow });
        }

        await ctx.SaveChangesAsync();
        return w;
    }

    [Fact]
    public async Task Off_by_default_a_low_clearance_user_still_sees_a_high_label()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var c = CreateContext(connection)) await c.Database.EnsureCreatedAsync();

        var w = await SeedAsync(connection, enforce: false, userClearance: 0);
        using var ctx = CreateContext(connection, w.TenantId);
        var calc = new EffectiveRightsCalculator(ctx);

        Assert.True((await calc.GetEffectiveRightsAsync(w.UserId, w.ConfidentialDocId)).CanSee);
    }

    [Fact]
    public async Task Enforced_a_low_clearance_user_is_denied_a_higher_label_but_keeps_lower_and_unlabelled()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var c = CreateContext(connection)) await c.Database.EnsureCreatedAsync();

        // Clearance 1 (Public): sees Public (rank 1) + unlabelled (rank 0), not Confidential (rank 3).
        var w = await SeedAsync(connection, enforce: true, userClearance: 1);
        using var ctx = CreateContext(connection, w.TenantId);
        var calc = new EffectiveRightsCalculator(ctx);

        Assert.False((await calc.GetEffectiveRightsAsync(w.UserId, w.ConfidentialDocId)).CanSee);
        Assert.True((await calc.GetEffectiveRightsAsync(w.UserId, w.PublicDocId)).CanSee);
        Assert.True((await calc.GetEffectiveRightsAsync(w.UserId, w.UnlabelledDocId)).CanSee);
    }

    [Fact]
    public async Task Enforced_clearance_from_a_group_lets_a_member_see_the_higher_label()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var c = CreateContext(connection)) await c.Database.EnsureCreatedAsync();

        // The user's own clearance is 0, but a group they belong to has clearance 3 → effective 3 → sees Confidential.
        var w = await SeedAsync(connection, enforce: true, userClearance: 0, groupClearance: 3);
        using (var m = CreateContext(connection, w.TenantId))
        {
            m.GroupMemberships.Add(new GroupMembership { TenantId = w.TenantId, GroupId = w.GroupId, UserId = w.UserId });
            await m.SaveChangesAsync();
        }

        using var ctx = CreateContext(connection, w.TenantId);
        var calc = new EffectiveRightsCalculator(ctx);

        Assert.True((await calc.GetEffectiveRightsAsync(w.UserId, w.ConfidentialDocId)).CanSee);
    }

    [Fact]
    public async Task Enforced_a_tenant_admin_bypasses_clearance()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var c = CreateContext(connection)) await c.Database.EnsureCreatedAsync();

        var w = await SeedAsync(connection, enforce: true, userClearance: 0);
        using var ctx = CreateContext(connection, w.TenantId);
        var calc = new EffectiveRightsCalculator(ctx);

        // The admin has clearance 0 but IsTenantAdmin, so sees the Confidential doc regardless.
        Assert.True((await calc.GetEffectiveRightsAsync(w.AdminId, w.ConfidentialDocId)).CanSee);
    }
}
