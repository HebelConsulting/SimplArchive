using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// How often a document reminder repeats (ADR "Document reminders"). None = one-shot.
public enum ReminderRecurrence
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}

// A user-set reminder that puts a document back in front of someone on a future date
// (ADR "Document reminders"). Assignable — the target (UserId) may differ from the creator
// (CreatedByUserId). A background sweep fires a notification on the due date; a one-shot then stamps FiredAt,
// a recurring one advances RemindAt to the next occurrence. ITenantScoped; append/cancel + the sweep's
// RemindAt/FiredAt update (not versioned/soft-deletable/IConcurrencyTracked).
public class DocumentReminder : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The person reminded (FK Cascade) — reminders are per-User (a ServiceAccount has no in-app intray).
    public Guid UserId { get; set; }

    public Guid DocumentId { get; set; }

    // When the reminder next fires (UTC). For a recurring reminder this advances after each fire.
    public DateTimeOffset RemindAt { get; set; }

    public string? Note { get; set; }

    public ReminderRecurrence Recurrence { get; set; }

    // Who set it — may differ from UserId when the reminder was assigned to someone else.
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Set to the fire instant when a one-shot (Recurrence == None) reminder has fired (null = still pending).
    // A recurring reminder keeps this null and advances RemindAt instead.
    public DateTimeOffset? FiredAt { get; set; }
}
