namespace SimplArchive.Client.Services;

// Lets the app-shell (MainLayout — the notifications bell) ask the workbench page (Home) to open a document
// (ADR "Notification viewer + click-through"). A singleton the two components share, since the bell lives in
// the shell and the tree/tab navigation lives in the body. Home subscribes; the bell raises the request.
public sealed class AppNavigationState
{
    // documentId + the document's parent folder (null when the document is a repository root).
    public event Func<Guid, Guid?, Task>? OpenDocumentRequested;

    public Task RequestOpenDocumentAsync(Guid documentId, Guid? parentId) =>
        OpenDocumentRequested?.Invoke(documentId, parentId) ?? Task.CompletedTask;

    // A deep link (#761) arrives BEFORE Home exists — the /go/{id} lander runs, then navigates to "/", and
    // only then does Home subscribe above. So the lander PARKS the target here and Home consumes it once,
    // right after its own load. An event alone cannot serve an entry point; a parked value cannot go stale,
    // because consuming clears it.
    public (Guid DocumentId, Guid? ParentId)? PendingDeepLink { get; set; }

    public (Guid DocumentId, Guid? ParentId)? TakePendingDeepLink()
    {
        var pending = PendingDeepLink;
        PendingDeepLink = null;
        return pending;
    }
}
