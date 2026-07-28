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
}
