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

    /// <summary>Disposable now — as the SERVER says, not as this row recomputes it.</summary>
    /// <remarks>
    /// This was <c>Overdue &amp;&amp; !SuspendedByHold</c>, a re-derivation of the server's rule that omitted its
    /// third condition: the tenant-wide <c>RequireDispositionReview</c> policy, for which the server
    /// deliberately withholds <c>dispose</c> (ADR 0385/0543). So with review switched on, an overdue un-held row
    /// showed an ENABLED Dispose whose click reached <c>RequireHref</c> and threw
    /// <c>InvalidOperationException</c> — not the <c>ApiActionException</c> the command catches, so it escaped
    /// to the crash guard and the user got a crash dialog for a records action (#870).
    ///
    /// The row already carries <c>Item</c> whole precisely so its actions can follow the server's answer.
    /// </remarks>
    public bool CanDispose => Item.Href("dispose") is not null;

    public string Status => SuspendedByHold ? "Suspended (legal hold)" : Overdue ? "Due for disposition" : "Scheduled";
}
