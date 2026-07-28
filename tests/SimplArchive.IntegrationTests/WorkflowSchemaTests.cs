using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Validates the workflow schema (ADR "Workflow / document state model", 0009): the WorkflowState/WorkflowTransition
// tables, their CHECK constraints (rejection reason, exactly-one performer), and the one-state-per-version index.
public class WorkflowSchemaTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _serviceAccountId = Guid.NewGuid();
    private readonly Guid _versionId = Guid.NewGuid();

    private SimplArchiveDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task<SqliteConnection> SeedAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        using var ctx = CreateContext(connection);
        var now = DateTimeOffset.UtcNow;
        var docId = Guid.NewGuid();
        ctx.Tenants.Add(new Tenant { Id = _tenantId, Name = "T", Status = TenantStatus.Active, CreatedAt = now });
        ctx.Users.Add(new User { Id = _userId, TenantId = _tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = now });
        ctx.ServiceAccounts.Add(new SimplArchive.Domain.ServiceAccounts.ServiceAccount { Id = _serviceAccountId, TenantId = _tenantId, Name = "svc", OpenIddictApplicationClientId = "c", IsActive = true, CreatedAt = now });
        ctx.Documents.Add(new Document { Id = docId, TenantId = _tenantId, Name = "Doc", CreatedByUserId = _userId, CreatedAt = now });
        ctx.DocumentVersions.Add(new DocumentVersion
        {
            Id = _versionId,
            TenantId = _tenantId,
            DocumentId = docId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = "hash",
            ObjectKey = "key",
            CreatedByUserId = _userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        });
        await ctx.SaveChangesAsync();
        return connection;
    }

    private WorkflowState AddState(SimplArchiveDbContext ctx, WorkflowStatus status)
    {
        var state = new WorkflowState
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            DocumentVersionId = _versionId,
            Status = status,
            AssignedToUserId = status == WorkflowStatus.InReview ? _userId : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        ctx.WorkflowStates.Add(state);
        return state;
    }

    [Fact]
    public async Task Allows_one_workflow_state_per_version_and_rejects_a_second()
    {
        using var connection = await SeedAsync();

        using (var ctx = CreateContext(connection))
        {
            AddState(ctx, WorkflowStatus.InReview);
            Assert.Equal(1, await ctx.SaveChangesAsync());
        }

        using (var ctx = CreateContext(connection))
        {
            AddState(ctx, WorkflowStatus.Approved); // same version → violates the unique index
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Requires_a_rejection_reason_only_for_a_Rejected_transition()
    {
        using var connection = await SeedAsync();
        Guid stateId;

        using (var ctx = CreateContext(connection))
        {
            stateId = AddState(ctx, WorkflowStatus.Rejected).Id;
            await ctx.SaveChangesAsync();
        }

        // Rejected with no reason → rejected by the CHECK.
        using (var ctx = CreateContext(connection))
        {
            ctx.WorkflowTransitions.Add(NewTransition(stateId, WorkflowStatus.InReview, WorkflowStatus.Rejected, reason: null));
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }

        // Approved with a reason → also rejected (a reason only belongs on a rejection).
        using (var ctx = CreateContext(connection))
        {
            ctx.WorkflowTransitions.Add(NewTransition(stateId, WorkflowStatus.InReview, WorkflowStatus.Approved, reason: "nope"));
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }

        // Rejected with a reason → allowed.
        using (var ctx = CreateContext(connection))
        {
            ctx.WorkflowTransitions.Add(NewTransition(stateId, WorkflowStatus.InReview, WorkflowStatus.Rejected, reason: "needs the totals fixed"));
            Assert.Equal(1, await ctx.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Requires_exactly_one_performer_on_a_transition()
    {
        using var connection = await SeedAsync();
        Guid stateId;

        using (var ctx = CreateContext(connection))
        {
            stateId = AddState(ctx, WorkflowStatus.InReview).Id;
            await ctx.SaveChangesAsync();
        }

        // Neither performer set → rejected.
        using (var ctx = CreateContext(connection))
        {
            var t = NewTransition(stateId, WorkflowStatus.Draft, WorkflowStatus.InReview);
            t.PerformedByUserId = null;
            t.PerformedByServiceAccountId = null;
            ctx.WorkflowTransitions.Add(t);
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }

        // A ServiceAccount performer → allowed (exactly one).
        using (var ctx = CreateContext(connection))
        {
            var t = NewTransition(stateId, WorkflowStatus.Draft, WorkflowStatus.InReview);
            t.PerformedByUserId = null;
            t.PerformedByServiceAccountId = _serviceAccountId;
            ctx.WorkflowTransitions.Add(t);
            Assert.Equal(1, await ctx.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Round_trips_a_state_and_its_history_under_the_tenant_filter()
    {
        using var connection = await SeedAsync();
        Guid stateId;

        using (var ctx = CreateContext(connection))
        {
            var state = AddState(ctx, WorkflowStatus.InReview);
            stateId = state.Id;
            await ctx.SaveChangesAsync();
            ctx.WorkflowTransitions.Add(NewTransition(stateId, WorkflowStatus.Draft, WorkflowStatus.InReview, assignedTo: _userId));
            await ctx.SaveChangesAsync();
        }

        using (var ctx = CreateContext(connection))
        {
            var state = await ctx.WorkflowStates.SingleAsync(w => w.DocumentVersionId == _versionId);
            Assert.Equal(WorkflowStatus.InReview, state.Status);
            Assert.Equal(_userId, state.AssignedToUserId);

            var history = await ctx.WorkflowTransitions.Where(t => t.WorkflowStateId == stateId).ToListAsync();
            Assert.Single(history);
            Assert.Equal(WorkflowStatus.InReview, history[0].ToStatus);
            Assert.Equal(_userId, history[0].AssignedToUserId);
        }
    }

    private WorkflowTransition NewTransition(Guid stateId, WorkflowStatus from, WorkflowStatus to, string? reason = null, Guid? assignedTo = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        WorkflowStateId = stateId,
        FromStatus = from,
        ToStatus = to,
        RejectionReason = reason,
        AssignedToUserId = assignedTo,
        PerformedByUserId = _userId,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
