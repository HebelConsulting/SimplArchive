using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Workflow;

// The current workflow state of a single DocumentVersion (ADR "Workflow / document state model", 0009 — status
// is per-version). Created when a Confirmed version is submitted for review (opt-in, slice 1) and updated
// in place as it transitions; the append-only WorkflowTransition history records how it got there. A version
// with no WorkflowState row is implicitly Draft ("not in workflow").
public class WorkflowState : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // One workflow state per version (unique). Cascades when the version/document is deleted.
    public Guid DocumentVersionId { get; set; }

    public WorkflowStatus Status { get; set; }

    // The current reviewer while InReview — a specific User (ADR 0009: a task is assigned to a specific
    // person, not any holder of a role). Null once resolved (Approved/Rejected/Released).
    public Guid? AssignedToUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // Review deadline (ADR "Workflow escalation / SLA reminders") = submitted-at + the document's mask
    // ReviewSlaDays. Null when the document's mask has no SLA. A review is overdue when now > DueAt.
    public DateTimeOffset? DueAt { get; set; }

    // Escalation-sweep bookkeeping — set once the pre-deadline reminder / the overdue escalation has fired, so
    // the sweep doesn't re-notify. Cleared on (re)submit for a fresh deadline.
    public DateTimeOffset? ReminderSentAt { get; set; }

    public DateTimeOffset? EscalatedAt { get; set; }
}
