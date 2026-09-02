using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Share links (ADR 0546): the links on one document, and the caller's own links across the tenant.
//
// The two dialog hooks are settable CALLBACKS rather than constructor arguments, and deliberately so
// (ADR 0730): a view-model does not open windows, and the view that would supply them does not exist yet when
// this is built. The rule's own test applies -- a forgotten dialog callback disables a visible button, which
// somebody reports the same day, so it is the LOUD kind of omission that constructor injection does not need
// to cure.
//
// It came out of a heading reading "Author identity card", which was true of the three members before it.
public sealed partial class MainWindowViewModel
{
    // External links (ADR 0546). Set by MainWindow so the view-model can raise the dialog without knowing about
    // Avalonia — the same indirection the reminder dialog uses.
    public Func<ExternalLinksDialogViewModel, Task>? ShowExternalLinksDialog { get; set; }

    // The per-link detail window the links dialog opens for its "Show" action (ADR 0546). Hosted here for the
    // same reason as the dialog above: a view-model does not open windows.
    public Func<ExternalLinkDetailDialogViewModel, Task>? ShowExternalLinkDetailDialog { get; set; }

    // The cross-document collection's href, from the API root's "externalLinks" rel (ADR 0543). Null until the
    // root is read, and if the server never offers it the command simply does nothing — absence of a rel is the
    // answer, not something to work around.
    private string? _myExternalLinksHref;

    // Drives the ribbon button. Without this the command existed but nothing invoked it — the dialog was
    // unreachable on the desktop, which is how a shipped feature stays invisible.
    [ObservableProperty] private bool _hasMyExternalLinks;

    // The selected document's own links collection, from the "external-links" rel on the DOCUMENT resource
    // (issue #385). Null when the tenant has the feature off or the caller may not share this document, which is
    // exactly what hides the affordance — a missing rel means "not available to you, here, now".
    private string? _detailExternalLinksHref;

    private IReadOnlyDictionary<string, string>? _detailLinks;

    // The advertised href for a rel on the document currently shown in the detail pane. Throws rather than
    // composing: a rel the resource did not advertise means the action is not available here (ADR 0543).
    private string DetailHref(string rel) =>
        _detailLinks is not null && _detailLinks.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The '{rel}' rel was not advertised for the open document.");

    private string _detailDocumentName = string.Empty;

    [ObservableProperty] private bool _canShareDocument;

    // "Share this document" — the per-document dialog: create a link for THIS document, list the live ones,
    // extend or revoke. Same view-model as the cross-document view, which only differs by offering creation.
    [RelayCommand]
    private async Task ShowDocumentExternalLinksAsync()
    {
        if (_api is null || ShowExternalLinksDialog is null || _detailExternalLinksHref is not { } href)
        {
            return;
        }

        var perDocument = new ExternalLinksDialogViewModel(_api, href, _detailDocumentName);
        perDocument.ShowDetailDialog = ShowExternalLinkDetailDialog;
        await ShowExternalLinksDialog(perDocument);
    }

    // "My external links" — everything the caller has shared, across documents. The collection is a top-level
    // resource, so its href is stable rather than per-document.
    [RelayCommand]
    private async Task ShowMyExternalLinksAsync()
    {
        if (_api is null || ShowExternalLinksDialog is null)
        {
            return;
        }

        if (_myExternalLinksHref is not { } href)
        {
            return;
        }

        var dialog = new ExternalLinksDialogViewModel(_api, href, Strings.Get("ExtLinkMine"), crossDocument: true);
        dialog.ShowDetailDialog = ShowExternalLinkDetailDialog;

        // "Go to" leaves the dialog and moves the workbench to the shared document — the same end state as
        // browsing to it by hand, which is what the reader of a cross-document list is usually after.
        Guid? goToDocument = null;
        string? goToDocumentHref = null;
        string? goToParentHref = null;
        dialog.GoToDocument = (documentId, documentHref, parentHref) =>
        {
            goToDocument = documentId;
            goToDocumentHref = documentHref;
            goToParentHref = parentHref;
            dialog.RequestClose?.Invoke();
        };

        await ShowExternalLinksDialog(dialog);

        if (goToDocument is { } target)
        {
            // The parent is where the document lives; without one it IS a repository root, so open the document's
            // own address directly. Both are the ROW's advertised addresses (ADR 0555, #443).
            await OpenFolderAsync(
                goToParentHref ?? goToDocumentHref
                ?? throw new InvalidOperationException("The external-link row advertised no 'document' rel (ADR 0543/0555)."),
                target);
        }
    }
}
