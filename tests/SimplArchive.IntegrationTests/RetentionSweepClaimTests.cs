using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.LegalHolds;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Retention;

namespace SimplArchive.IntegrationTests;

// The retention sweep claims before disposing (#1442, ADR 0836).
//
// WHAT THE DEFECT WAS, precisely — because it is not the one the other sweeps had. There was never a duplicate:
// `_dbContext.Documents` carries the SoftDeleteFilter, so once one instance disposed a document the other's
// `SingleAsync(d => d.Id == candidate.Id)` matched ZERO rows and threw "Sequence contains no elements" BEFORE
// either the soft delete or the audit event. The sweep was self-limiting by accident.
//
// What it cost was THE REST OF THE PAGE and the explanation: RetentionWorker catches Exception and logs
// "Retention sweep failed; will retry next interval", so the losing instance abandoned every remaining candidate
// and the operator got a warning naming neither the document nor the real cause. So these tests assert the two
// things that were actually wrong — the sweep does not throw, and it keeps going.
public class RetentionSweepClaimTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    private sealed class NoOpIndexQueue : IDocumentIndexQueue
    {
        public Task EnqueueAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnqueueManyAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Runs a SECOND sweep from inside the first one's index enqueue — which is the interleaving that produced the
    /// throw.
    /// </summary>
    /// <remarks>
    /// The enqueue fires immediately after a claim, so at that moment the outer sweep holds a candidate page it
    /// read BEFORE anything was disposed, and has disposed the first of them. The inner sweep then reads a FRESH
    /// page (the disposed one is filtered out), disposes the next candidate, and returns — leaving the outer
    /// holding a page whose remaining entry is now gone. That is exactly the stale page two instances produce, and
    /// the only moment at which the old `SingleAsync` could find nothing.
    /// </remarks>
    private sealed class ReentrantIndexQueue : IDocumentIndexQueue
    {
        private readonly SqliteConnection _connection;
        private bool _entered;

        public ReentrantIndexQueue(SqliteConnection connection) => _connection = connection;

        public int DisposedByTheSecondSweep { get; private set; } = -1;

        public async Task EnqueueAsync(Guid documentId, CancellationToken cancellationToken = default)
        {
            if (_entered)
            {
                return;
            }

            _entered = true;

            // A separate accessor, because the inner sweep sets the tenant context itself and would otherwise
            // leave the outer one pointing wherever it finished.
            var innerTenant = new CurrentTenantAccessor();
            using var context = CreateContext(_connection, innerTenant);
            var service = new RetentionService(
                context, innerTenant, new LegalHoldService(context), new NoOpIndexQueue(), new CountingAuditRecorder());
            DisposedByTheSecondSweep = await service.SweepAsync(cancellationToken);
        }

        public Task EnqueueManyAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CountingAuditRecorder : IAuditRecorder
    {
        public List<Guid> DisposedTargets { get; } = [];

        public Task RecordAsync(string action, string? targetType = null, Guid? targetId = null,
            string? targetName = null, string? details = null, Guid? tenantId = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordForActorAsync(AuditActorType actorType, Guid actorId, string actorName, Guid tenantId,
            string action, string? targetType = null, Guid? targetId = null, string? targetName = null,
            string? details = null, CancellationToken cancellationToken = default)
        {
            if (targetId is { } id)
            {
                DisposedTargets.Add(id);
            }

            return Task.CompletedTask;
        }
    }

    private static Document Doc(Guid tenantId, Guid userId, Guid maskVersionId, string name, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        MaskVersionId = maskVersionId,
        Name = name,
        CreatedByUserId = userId,
        CreatedAt = createdAt,
    };

    [Fact]
    public async Task A_sweep_whose_candidate_was_disposed_by_another_instance_keeps_going()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = "u@acme.test",
            DisplayName = "U",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var mask = new Mask { Id = Guid.NewGuid(), TenantId = tenant.Id, CreatedAt = DateTimeOffset.UtcNow };
        var maskVersion = new MaskVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            MaskId = mask.Id,
            Name = "Retained",
            RetentionYears = 5,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // TWO expired candidates, which is what makes this test about the PAGE rather than about one row: the
        // outer sweep reads both, disposes the first, and finds the second gone.
        var old = DateTimeOffset.UtcNow.AddYears(-10);
        var first = Doc(tenant.Id, user.Id, maskVersion.Id, "expired-a", old);
        var second = Doc(tenant.Id, user.Id, maskVersion.Id, "expired-b", old);

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Masks.Add(mask);
            seed.MaskVersions.Add(maskVersion);
            seed.Documents.AddRange(first, second);
            await seed.SaveChangesAsync();
        }

        var reentrant = new ReentrantIndexQueue(connection);
        var audit = new CountingAuditRecorder();
        int disposedByTheFirstSweep;
        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = new RetentionService(act, tenantAccessor, new LegalHoldService(act), reentrant, audit);

            // IT DOES NOT THROW. Before the claim this was `Sequence contains no elements`, which RetentionWorker
            // turned into "Retention sweep failed" — naming neither the document nor the cause.
            disposedByTheFirstSweep = await service.SweepAsync();
        }

        // The inner sweep took the second document while the outer was mid-page.
        Assert.Equal(1, reentrant.DisposedByTheSecondSweep);

        // And the outer disposed only its own — it did not count a document another instance handled.
        Assert.Equal(1, disposedByTheFirstSweep);

        // BOTH are disposed, which is the property the abandoned page destroyed: the loser used to give up on
        // everything after the collision, and the documents behind it waited for a later tick.
        using var read = CreateContext(connection, tenantAccessor);
        var deleted = await read.Documents.IgnoreQueryFilters()
            .Where(d => d.DeletedAt != null)
            .Select(d => d.Id)
            .ToListAsync();
        Assert.Equal(2, deleted.Count);
        Assert.Contains(first.Id, deleted);
        Assert.Contains(second.Id, deleted);

        // The outer sweep recorded exactly ONE disposal — its own. Not two (it did not re-dispose the document the
        // inner sweep took) and not zero (it did not abandon its page).
        //
        // WHICH document that is, is deliberately not asserted: the candidate query is `orderby d.Id` over random
        // Guids, so which of the two the outer sweep reaches first is a COIN FLIP. An earlier version of this test
        // asserted `[first.Id]` and passed on the first run purely by luck — exactly the shape of a test that
        // turns main red later for a reason unrelated to the change that exposed it.
        var recorded = Assert.Single(audit.DisposedTargets);
        Assert.Contains(recorded, new[] { first.Id, second.Id });
    }

    [Fact]
    public async Task Disposing_moves_the_concurrency_token_so_a_stale_editor_is_still_refused()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = "u@acme.test",
            DisplayName = "U",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var mask = new Mask { Id = Guid.NewGuid(), TenantId = tenant.Id, CreatedAt = DateTimeOffset.UtcNow };
        var maskVersion = new MaskVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            MaskId = mask.Id,
            Name = "Retained",
            RetentionYears = 5,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var expired = Doc(tenant.Id, user.Id, maskVersion.Id, "expired", DateTimeOffset.UtcNow.AddYears(-10));

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Masks.Add(mask);
            seed.MaskVersions.Add(maskVersion);
            seed.Documents.Add(expired);
            await seed.SaveChangesAsync();
        }

        Guid before;
        using (var read = CreateContext(connection, tenantAccessor))
        {
            before = (await read.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == expired.Id)).ConcurrencyToken;
        }

        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = new RetentionService(
                act, tenantAccessor, new LegalHoldService(act), new NoOpIndexQueue(), new CountingAuditRecorder());
            Assert.Equal(1, await service.SweepAsync());
        }

        // WHY THIS IS ASSERTED. Document is IConcurrencyTracked, and SaveChanges regenerates the token for every
        // Modified tracked entity — ExecuteUpdate does NOT, because the ChangeTracker never sees the row. So
        // converting this write to a claim would have silently retired a 412 that fires today: a caller holding
        // the tag from before the disposal would write successfully to a document now in the recycle bin instead
        // of being told to reload. The claim therefore sets the token itself, and this is what says so.
        using var after = CreateContext(connection, tenantAccessor);
        var disposed = await after.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == expired.Id);
        Assert.NotNull(disposed.DeletedAt);
        Assert.NotEqual(before, disposed.ConcurrencyToken);
    }
}
