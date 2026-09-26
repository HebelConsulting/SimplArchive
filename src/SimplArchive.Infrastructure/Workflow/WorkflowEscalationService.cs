using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Workflow;

// Fires SLA reminders + overdue escalations for in-review workflow tasks (ADR "Workflow escalation / SLA
// reminders"). Notifications are written directly (not via INotificationService, which is request-actor-scoped
// with a self-skip that doesn't apply to this system sweep). Idempotent: ReminderSentAt / EscalatedAt stop a
// task being re-notified. Registered scoped; the hosted WorkflowEscalationWorker calls it on a timer.
public sealed class WorkflowEscalationService : IWorkflowEscalationService
{
    // The reminder fires once the review enters its final day before the deadline (and isn't yet overdue).
    private static readonly TimeSpan ReminderLead = TimeSpan.FromDays(1);
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];

    private readonly SimplArchiveDbContext _dbContext;

    public WorkflowEscalationService(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // In-review tasks with a deadline still needing a reminder or an escalation. Only null-checks + the
        // enum in SQL (SQLite can't translate a DateTimeOffset comparison there); the DueAt comparison is done
        // in memory. The set is bounded by the number of active reviews with SLAs.
        var candidates = await _dbContext.WorkflowStates
            .IgnoreQueryFilters(TenantFilterOnly)
            .Where(w => w.Status == WorkflowStatus.InReview && w.DueAt != null
                && (w.EscalatedAt == null || w.ReminderSentAt == null))
            .ToListAsync(cancellationToken);

        var acted = 0;
        foreach (var state in candidates)
        {
            var due = state.DueAt!.Value;

            var escalating = state.EscalatedAt is null && now >= due;
            var reminding = !escalating && state.ReminderSentAt is null && now >= due - ReminderLead && now < due;
            if (!escalating && !reminding)
            {
                continue;
            }

            // ONE state, ONE transaction — the claim and the notifications commit together, and no row lock is
            // held across states (a batch-wide transaction is how two sweeps deadlock). See ClaimAsync.
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (!await ClaimAsync(state, escalating, now, cancellationToken))
            {
                // The other instance's sweep won this one. The normal outcome of two instances sweeping
                // (ADR 0808), not an error.
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            if (escalating)
            {
                await EscalateAsync(state, now, cancellationToken);
            }
            else
            {
                await RemindAsync(state, now, cancellationToken);
            }

            // No bookkeeping is written here — the CLAIM already did it, atomically, and the ChangeTracker
            // cannot see an ExecuteUpdate, so writing it again would be a second update from stale state.
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            acted++;
        }

        return acted;
    }

    /// <summary>
    /// Takes this workflow state's escalation (or reminder) for THIS sweep, atomically, and says whether it was
    /// won. Escalating sets both markers, exactly as the sweep used to: that is what drops the state out of
    /// future candidate scans rather than leaving it to be reminded about after it has already escalated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A compare-and-swap on the marker that was read, so the database picks the winner and the loser gets 0
    /// rows. Same mechanism as the reminder sweep's, and for the same reason (#1425): the sweep used to read
    /// candidates whose marker was null, send the notifications, stamp the markers and save once at the end —
    /// the correct transactional shape (ADR 0794) with no exclusion, so two sweeps both read null before either
    /// committed and both acted.
    /// </para>
    /// <para>
    /// <b>Not hypothetical, and worse here than for a reminder.</b> ADR 0808 runs two app instances, each
    /// registering this sweep's worker on its own timer, so the overlap is the arrangement. And an escalation
    /// notifies the reviewer, the submitter AND every tenant administrator — so one overlapped sweep duplicates
    /// it for the whole admin group, not for one person.
    /// </para>
    /// <para>
    /// Deliberately not shared with the reminder sweep's version: what differs is the WHOLE body — which
    /// markers, and which of two actions they gate — so a common helper would be a passthrough to
    /// <c>ExecuteUpdateAsync</c> wearing a name, and the pattern is what is worth repeating, not the line.
    /// </para>
    /// </remarks>
    private async Task<bool> ClaimAsync(
        WorkflowState state, bool escalating, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = _dbContext.WorkflowStates
            .IgnoreQueryFilters(TenantFilterOnly)
            .Where(w => w.Id == state.Id);

        var won = escalating
            ? await rows.Where(w => w.EscalatedAt == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(w => w.EscalatedAt, (DateTimeOffset?)now)
                        .SetProperty(w => w.ReminderSentAt, w => w.ReminderSentAt ?? now),
                    cancellationToken)
            : await rows.Where(w => w.ReminderSentAt == null)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(w => w.ReminderSentAt, (DateTimeOffset?)now), cancellationToken);

        return won == 1;
    }

    private async Task EscalateAsync(WorkflowState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await DocumentAsync(state, cancellationToken) is not { } doc)
        {
            return; // document no longer live — nothing to escalate (bookkeeping still set by the caller)
        }

        var recipients = new HashSet<Guid>();
        if (state.AssignedToUserId is { } reviewer) recipients.Add(reviewer);
        if (await SubmitterAsync(state.Id, cancellationToken) is { } submitter) recipients.Add(submitter);
        foreach (var admin in await TenantAdminsAsync(state.TenantId, cancellationToken)) recipients.Add(admin);

        foreach (var recipientId in recipients)
        {
            AddNotification(state.TenantId, recipientId, NotificationType.ReviewOverdue, "Review overdue", $"The review of '{doc.Name}' is overdue.", doc.Id, now);
        }
    }

    private async Task RemindAsync(WorkflowState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.AssignedToUserId is not { } reviewer || await DocumentAsync(state, cancellationToken) is not { } doc)
        {
            return;
        }

        AddNotification(state.TenantId, reviewer, NotificationType.ReviewReminder, "Review due soon", $"Your review of '{doc.Name}' is due soon.", doc.Id, now);
    }

    private void AddNotification(Guid tenantId, Guid recipientUserId, NotificationType type, string title, string body, Guid documentId, DateTimeOffset now) =>
        _dbContext.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RecipientUserId = recipientUserId,
            Type = type,
            Title = title,
            Body = body,
            DocumentId = documentId,
            CreatedAt = now,
        });

    // The (live) document behind a workflow state — its version → document; the Documents soft-delete filter
    // (kept, only the tenant filter is ignored) drops a recycled document so its stale review isn't escalated.
    private async Task<(Guid Id, string Name)?> DocumentAsync(WorkflowState state, CancellationToken cancellationToken)
    {
        var row = await (
            from v in _dbContext.DocumentVersions.IgnoreQueryFilters(TenantFilterOnly)
            where v.Id == state.DocumentVersionId
            join d in _dbContext.Documents.IgnoreQueryFilters(TenantFilterOnly) on v.DocumentId equals d.Id
            select new { d.Id, d.Name }).FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : (row.Id, row.Name);
    }

    // The performer of the latest Submit (→ In Review). Ordered client-side — SQLite (the test provider) can't
    // ORDER BY a DateTimeOffset; the Submit transitions for one state are few.
    private async Task<Guid?> SubmitterAsync(Guid workflowStateId, CancellationToken cancellationToken)
    {
        var submits = await _dbContext.WorkflowTransitions
            .IgnoreQueryFilters(TenantFilterOnly)
            .Where(t => t.WorkflowStateId == workflowStateId && t.ToStatus == WorkflowStatus.InReview)
            .Select(t => new { t.CreatedAt, t.PerformedByUserId })
            .ToListAsync(cancellationToken);

        return submits.OrderByDescending(t => t.CreatedAt).Select(t => t.PerformedByUserId).FirstOrDefault();
    }

    private Task<List<Guid>> TenantAdminsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _dbContext.Users
            .IgnoreQueryFilters(TenantFilterOnly)
            .Where(u => u.TenantId == tenantId && u.IsTenantAdmin && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
}
