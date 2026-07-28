namespace SimplArchive.Domain.Workflow;

// The fixed approval state machine for a document version (ADR "Workflow / document state model", 0009).
// Draft is the implicit "not yet submitted" state — in the opt-in model (slice 1) a version has NO
// WorkflowState row until it's submitted, at which point the row is created directly in InReview, so a
// persisted WorkflowState.Status is never Draft. Draft still appears as the FromStatus of the first Submit
// transition. Numeric values are referenced by the DB CHECK constraints — do not renumber.
public enum WorkflowStatus
{
    Draft = 0,
    InReview = 1,
    Approved = 2,
    Rejected = 3,
    Released = 4,
}
