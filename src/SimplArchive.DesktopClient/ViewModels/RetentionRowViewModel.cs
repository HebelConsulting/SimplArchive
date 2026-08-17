namespace SimplArchive.DesktopClient.ViewModels;

// A row in the Retention schedule tab (ADR "Retention policies (auto-disposition)" + "Retention
// review-before-disposition"). Carries the document id + override so the Dispose/Extend actions can act on it.
// Item is the row the server sent, carried whole so Dispose/Extend follow the addresses it advertised rather
// than rebuilding paths from a document id (ADR 0543/0555).
public sealed class RetentionRowViewModel(Guid documentId, string documentName, int retentionYears, string dispositionDate, bool overdue, bool suspendedByHold, string? retentionOverrideUntil, SimplArchive.DesktopClient.Services.LegalHoldsClient.RetentionItemInfo item)
{
    public SimplArchive.DesktopClient.Services.LegalHoldsClient.RetentionItemInfo Item { get; } = item;

    public Guid DocumentId { get; } = documentId;
    public string DocumentName { get; } = documentName;
    public string Retention { get; } = $"{retentionYears} years";
    public string DispositionDate { get; } = retentionOverrideUntil is { } o ? $"{dispositionDate} · retained until {o}" : dispositionDate;

    public bool Overdue { get; } = overdue;
    public bool SuspendedByHold { get; } = suspendedByHold;

    // Disposable now = past its (override-adjusted) disposition date and not legal-held.
    public bool CanDispose => Overdue && !SuspendedByHold;

    public string Status => SuspendedByHold ? "Suspended (legal hold)" : Overdue ? "Due for disposition" : "Scheduled";
}
