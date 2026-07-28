using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Workflow;

// One append-only entry in a version's workflow status history — a single transition (submit / approve /
// reject / release / resubmit). See ADR "Workflow / document state model" (0009) and ADR "Workflow rejection
// reason requirement" (0143), which puts the rejection reason on this record rather than the general comment
// thread.
public class WorkflowTransition : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkflowStateId { get; set; }

    public WorkflowStatus FromStatus { get; set; }

    public WorkflowStatus ToStatus { get; set; }

    // Required (non-blank) only when ToStatus is Rejected (ADR 0143) — enforced by a CHECK constraint and the
    // handler. Null for every other transition.
    public string? RejectionReason { get; set; }

    // The reviewer assigned by this transition (a Submit/Resubmit into InReview); null otherwise.
    public Guid? AssignedToUserId { get; set; }

    // Exactly one of PerformedByUserId/PerformedByServiceAccountId is set (CHECK) — same shape as
    // DocumentVersion's creator pair.
    public Guid? PerformedByUserId { get; set; }

    public Guid? PerformedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
