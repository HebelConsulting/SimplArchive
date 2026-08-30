using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The batched rights resolution (#858) must answer exactly what asking one document at a time answers.
//
// WHY THIS IS NOT A VACUOUS TEST, since the shape invites one. GetEffectiveRightsAsync now DELEGATES to the
// batch — so "batch == single" would compare the batch with itself and pass no matter how wrong the batching
// is. What it actually compares is a batch of N against N batches of ONE, and that is the real risk surface:
// the per-document rules are shared code that the ten existing EffectiveRights* files already exercise, while
// the parts only a multi-id call can get wrong are the AGGREGATION steps — mapping resolved scopes back to the
// original ids, grouping the one ACL read by scope, and subtracting the clearance-blocked ids from the set
// still walking. A batch of one exercises every one of those trivially; a batch of six with mixed depths does
// not.
//
// The fixture is built so that a plausible aggregation bug cannot pass: no two documents share a right set by
// accident, the tree has four distinct depths so the level-by-level walk runs several rounds with a shrinking
// id set, and one document resolves to a DIFFERENT governing scope than its siblings.
public class EffectiveRightsBatchParityTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    [Fact]
    public async Task A_batch_answers_exactly_what_asking_one_at_a_time_answers()
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        // The tree: root → a → d (inherit all the way), root → b (BREAKS) → c (inherits b's override).
        var root = Guid.NewGuid();
        var a = Guid.NewGuid();
        var d = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        // Off to the side: over-clearance, and inside somebody else's personal space.
        var secret = Guid.NewGuid();
        var foreign = Guid.NewGuid();

        var topSecretLabel = Guid.NewGuid();

        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = now, EnforceClearance = true });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "u@x.com", DisplayName = "U", CreatedAt = now, ClearanceRank = 1 });
            seed.Users.Add(new User { Id = otherUserId, TenantId = tenantId, Email = "o@x.com", DisplayName = "O", CreatedAt = now, ClearanceRank = 1 });
            seed.SensitivityLabelDefinitions.Add(new SensitivityLabelDefinition { Id = topSecretLabel, TenantId = tenantId, Name = "Restricted", Rank = 4, CreatedAt = now });

            seed.Documents.Add(new Document { Id = root, TenantId = tenantId, Name = "root", CreatedByUserId = userId, CreatedAt = now });
            seed.Documents.Add(new Document { Id = a, TenantId = tenantId, ParentId = root, Name = "a", CreatedByUserId = userId, CreatedAt = now });
            seed.Documents.Add(new Document { Id = d, TenantId = tenantId, ParentId = a, Name = "d", CreatedByUserId = userId, CreatedAt = now });
            seed.Documents.Add(new Document { Id = b, TenantId = tenantId, ParentId = root, Name = "b", CreatedByUserId = userId, CreatedAt = now, BreaksInheritance = true });
            seed.Documents.Add(new Document { Id = c, TenantId = tenantId, ParentId = b, Name = "c", CreatedByUserId = userId, CreatedAt = now });
            seed.Documents.Add(new Document { Id = secret, TenantId = tenantId, ParentId = root, Name = "secret", CreatedByUserId = userId, CreatedAt = now, SensitivityLabelId = topSecretLabel });
            seed.Documents.Add(new Document { Id = foreign, TenantId = tenantId, Name = "foreign", CreatedByUserId = otherUserId, CreatedAt = now, PersonalRootOwnerId = otherUserId });

            // Deliberately different right sets, so swapping two rows' answers cannot look correct.
            seed.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = root, UserId = userId, CanSee = true, CanReadContent = true, CanDelete = true, CreatedAt = now });
            seed.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = b, UserId = userId, CanSee = true, CanEditIndexData = true, CanMove = true, CreatedAt = now });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        Guid[] all = [root, a, d, b, c, secret, foreign];

        var batched = await calculator.GetEffectiveRightsForManyAsync(userId, all);

        Assert.Equal(all.Length, batched.Count);
        foreach (var id in all)
        {
            var oneAtATime = await calculator.GetEffectiveRightsAsync(userId, id);
            Assert.Equal(oneAtATime, batched[id]);
        }

        // And the fixture really did produce the variety the parity claim rests on — otherwise the loop above
        // could be comparing seven identical answers and reporting agreement about nothing.
        Assert.True(batched[root].CanDelete);                       // own grant
        Assert.True(batched[d].CanDelete);                          // inherited from root, three levels up
        Assert.False(batched[b].CanDelete);                         // its own override replaces root's grant
        Assert.True(batched[b].CanMove);
        Assert.Equal(batched[b], batched[c]);                       // c inherits the override, not root
        Assert.NotEqual(batched[root], batched[b]);                 // two distinct governing scopes in one page
        Assert.False(batched[secret].CanSee);                       // clearance rank 4 > the user's 1
        Assert.False(batched[foreign].CanSee);                      // no grant inside another's personal space
    }

    [Fact]
    public async Task A_deactivated_user_gets_no_rights_for_every_id_in_the_batch()
    {
        // The active-principal check runs FIRST and returns for the whole set at once (ADRs 0174/0153), which
        // is the one path that never reaches the per-document loop — so it needs saying separately.
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();

        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = now });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "u@x.com", DisplayName = "U", CreatedAt = now, IsActive = false });
            seed.Documents.Add(new Document { Id = one, TenantId = tenantId, Name = "one", CreatedByUserId = userId, CreatedAt = now });
            seed.Documents.Add(new Document { Id = two, TenantId = tenantId, ParentId = one, Name = "two", CreatedByUserId = userId, CreatedAt = now });
            seed.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = one, UserId = userId, CanSee = true, CanDelete = true, CreatedAt = now });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var batched = await new EffectiveRightsCalculator(context).GetEffectiveRightsForManyAsync(userId, [one, two]);

        Assert.Equal(2, batched.Count);
        Assert.All(batched.Values, r => Assert.False(r.CanSee));
        Assert.All(batched.Values, r => Assert.False(r.CanDelete));
    }

    [Fact]
    public async Task An_empty_request_asks_the_database_nothing()
    {
        // A page can legitimately be empty, and the batch must not read the user row (let alone throw on a
        // user that does not exist) just to answer "no documents".
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        using var context = CreateContext(connection, Guid.NewGuid());
        var batched = await new EffectiveRightsCalculator(context).GetEffectiveRightsForManyAsync(Guid.NewGuid(), []);

        Assert.Empty(batched);
    }
}
