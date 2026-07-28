namespace SimplArchive.Application.Abstractions;

// Sweeps in-review workflow tasks against their SLA deadline (ADR "Workflow escalation / SLA reminders"),
// firing a pre-deadline reminder to the reviewer and, once overdue, an escalation to the reviewer + submitter +
// the tenant's admins. Idempotent per task (bookkeeping timestamps stop re-notification). Run by a hosted
// worker on a timer, and callable directly (e.g. in tests). Spans all tenants. Returns the number of tasks
// acted on.
public interface IWorkflowEscalationService
{
    Task<int> SweepAsync(CancellationToken cancellationToken = default);
}
