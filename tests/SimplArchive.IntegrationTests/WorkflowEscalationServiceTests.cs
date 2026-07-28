using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Workflow;

namespace SimplArchive.IntegrationTests;

// Verifies the workflow escalation sweep (ADR "Workflow escalation / SLA reminders"): an overdue review
// escalates to the reviewer + submitter + tenant admins (once, idempotently); a near-due review sends a single
// reminder to the reviewer; a not-yet-near review does nothing.
public class WorkflowEscalationServiceTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    [Fact]
    public async Task Overdue_review_escalates_to_reviewer_submitter_and_admins_once()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var ids = await SeedReviewAsync(connection, tenantAccessor, dueAt: DateTimeOffset.UtcNow.AddDays(-1));

        using (var svc = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(1, await new WorkflowEscalationService(svc).SweepAsync());
        }

        tenantAccessor.TenantId = ids.TenantId;
        using (var read = CreateContext(connection, tenantAccessor))
        {
            var overdue = await read.Notifications.Where(n => n.Type == NotificationType.ReviewOverdue).ToListAsync();
            var recipients = overdue.Select(n => n.RecipientUserId).ToHashSet();
            Assert.Equal(new[] { ids.ReviewerId, ids.SubmitterId, ids.AdminId }.ToHashSet(), recipients);
            Assert.All(overdue, n => Assert.Equal(ids.DocumentId, n.DocumentId));
            Assert.NotNull(await read.WorkflowStates.Where(w => w.Id == ids.StateId).Select(w => w.EscalatedAt).SingleAsync());
        }

        // Idempotent — a second sweep does nothing more.
        using (var svc = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(0, await new WorkflowEscalationService(svc).SweepAsync());
        }

        using (var read = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(3, await read.Notifications.CountAsync(n => n.Type == NotificationType.ReviewOverdue));
        }
    }

    [Fact]
    public async Task Near_due_review_reminds_the_reviewer_only()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var ids = await SeedReviewAsync(connection, tenantAccessor, dueAt: DateTimeOffset.UtcNow.AddHours(12));

        using (var svc = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(1, await new WorkflowEscalationService(svc).SweepAsync());
        }

        tenantAccessor.TenantId = ids.TenantId;
        using var read = CreateContext(connection, tenantAccessor);
        var reminders = await read.Notifications.Where(n => n.Type == NotificationType.ReviewReminder).ToListAsync();
        Assert.Equal(ids.ReviewerId, Assert.Single(reminders).RecipientUserId);
        Assert.Empty(await read.Notifications.Where(n => n.Type == NotificationType.ReviewOverdue).ToListAsync());
    }

    [Fact]
    public async Task Not_yet_near_review_does_nothing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        await SeedReviewAsync(connection, tenantAccessor, dueAt: DateTimeOffset.UtcNow.AddDays(3));

        using var svc = CreateContext(connection, tenantAccessor);
        Assert.Equal(0, await new WorkflowEscalationService(svc).SweepAsync());
        Assert.Empty(await svc.Notifications.IgnoreQueryFilters().ToListAsync());
    }

    private sealed record Ids(Guid TenantId, Guid ReviewerId, Guid SubmitterId, Guid AdminId, Guid DocumentId, Guid StateId);

    // Seeds a tenant with a reviewer, a submitter, a tenant admin, a document + confirmed version, and an
    // In-Review WorkflowState (assigned to the reviewer, with the given deadline) plus a Submit transition by
    // the submitter.
    private static async Task<Ids> SeedReviewAsync(SqliteConnection connection, CurrentTenantAccessor tenantAccessor, DateTimeOffset dueAt)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T{Guid.NewGuid():N}", CreatedAt = now };
        var reviewer = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = $"rev-{Guid.NewGuid():N}@t.test", DisplayName = "Reviewer", CreatedAt = now };
        var submitter = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = $"sub-{Guid.NewGuid():N}@t.test", DisplayName = "Submitter", CreatedAt = now };
        var admin = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = $"adm-{Guid.NewGuid():N}@t.test", DisplayName = "Admin", IsTenantAdmin = true, CreatedAt = now };
        var document = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Invoice", CreatedByUserId = submitter.Id, CreatedAt = now };
        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = new string('0', 64),
            ObjectKey = "k",
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
            CreatedByUserId = submitter.Id,
            CreatedAt = now,
        };
        var state = new WorkflowState
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            DocumentVersionId = version.Id,
            Status = WorkflowStatus.InReview,
            AssignedToUserId = reviewer.Id,
            CreatedAt = now,
            UpdatedAt = now,
            DueAt = dueAt,
        };
        var transition = new WorkflowTransition
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            WorkflowStateId = state.Id,
            FromStatus = WorkflowStatus.Draft,
            ToStatus = WorkflowStatus.InReview,
            PerformedByUserId = submitter.Id,
            CreatedAt = now,
        };

        using var seed = CreateContext(connection, tenantAccessor);
        seed.Tenants.Add(tenant);
        seed.Users.AddRange(reviewer, submitter, admin);
        seed.Documents.Add(document);
        seed.DocumentVersions.Add(version);
        seed.WorkflowStates.Add(state);
        seed.WorkflowTransitions.Add(transition);
        await seed.SaveChangesAsync();

        return new Ids(tenant.Id, reviewer.Id, submitter.Id, admin.Id, document.Id, state.Id);
    }
}
