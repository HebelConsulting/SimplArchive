using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// References, and the folder-opening they need: "Go to ..." on a reference navigates to the target's real
// home, the references pane lists what points at an item, and promoting a reference makes that appearance the
// primary location.
//
// OpenFolderAsync is here rather than with the tree because these are its callers -- a reference's whole
// purpose is to send you somewhere else, so the navigation belongs beside the thing that asks for it.
//
// It came out of the "Bulk actions on the multi-selection" heading, which was true of its first 122 lines and
// described none of these 99 (#941). The tail is where these headings decay: the earlier tenant-settings
// section had the same shape, and reading a section to its END rather than its start is what finds them.
public sealed partial class MainWindowViewModel
{
    // "Go to …" on a reference: navigate the contents pane to the target's real home folder and select it.
    public async Task GoToReferenceAsync(NodeViewModel node)
    {
        if (_api is null || !node.IsReference)
        {
            return;
        }

        if (node.RealParentId is not null)
        {
            // REVEAL, not merely open (#1264). OpenFolderAsync moves the contents pane and says so itself —
            // "the tree isn't re-synced" — which left the list showing the target's real home while the tree
            // still pointed at the folder holding the shortcut. The tree's one job is to answer "where am I"
            // (ADR 0703), and after a Go to it was answering wrongly.
            //
            // The sibling path was right the whole time: a search hit reveals, because OpenSearchResultAsync
            // calls this same method. One act — take me to where this really lives — must not behave two ways
            // depending on which surface it started from.
            //
            // Both addresses come from the row: `self` is the TARGET document, `go-to` its real parent. The
            // reveal expands the ancestor chain, opens the folder, and selects the tree node without loading it
            // twice.
            await RevealDocumentInTreeAsync(
                node.Id,
                node.Links?.Href("self")
                ?? throw new InvalidOperationException($"The shortcut '{node.Name}' advertised no 'self' rel (ADR 0543/0555)."),
                node.Links?.Href("go-to")
                ?? throw new InvalidOperationException($"The shortcut '{node.Name}' advertised no 'go-to' rel (ADR 0543/0555)."));
        }
        else
        {
            // The target lives at the repository root — show the repository list.
            await LoadRootAsync();
        }
    }

    // Navigates the contents pane to a folder by id (shared by "Go to …" and the references dialog),
    // optionally selecting an item in it. Slice simplification — the breadcrumb is rebuilt as
    // Repositories / <folder> only (the read API doesn't expose full ancestry, and the tree isn't re-synced).
    /// <summary>
    /// Opens the folder behind an ADVERTISED address (#443) — what the payload-row consumers (a task, a
    /// notification, a reminder, a search hit) use, following the row's `parent`/`document` rel instead of
    /// handing a bare id back into the address turn. ONE read serves the name, the id and the collections,
    /// where the id path costs two.
    /// </summary>
    public async Task OpenFolderAsync(string folderHref, Guid? selectTargetId = null)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var doc = await _api.GetDocumentByAddressAsync(folderHref);
            await OpenLoadedFolderAsync(doc.Id, doc.Name, doc.Links, selectTargetId);
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrOpenFolder"), e.Message));
        }
    }

    // The shared tail of both opens: contents, breadcrumbs, selection.
    private async Task OpenLoadedFolderAsync(Guid folderId, string name, LinkMap? folderLinks, Guid? selectTargetId)
    {
        await LoadFolderContentsAsync(folderId, folderLinks);
        await SetBreadcrumbFromAncestorsAsync(folderId, name, folderLinks);
        if (selectTargetId is { } targetId)
        {
            // Prefer the item's real row; fall back to its reference (shortcut) row when the folder holds only
            // a shortcut (a referencing folder) — selecting a reference loads the target document for viewing.
            SelectedItem = Items.FirstOrDefault(i => i.Id == targetId && !i.IsReference)
                ?? Items.FirstOrDefault(i => i.Id == targetId);
        }
    }

    /// <summary>
    /// Rebuilds the breadcrumb as the folder's REAL path, from the ancestors the resource advertises (#1266).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to add exactly two crumbs — <c>Repositories / &lt;folder&gt;</c> — however deep the folder
    /// actually sat, so a document filed four levels down reported a two-level path. That is not merely
    /// incomplete: it says the folder is a child of the repositories root, which is a different and wrong
    /// answer to the question the breadcrumb exists to answer (ADR 0703, "where am I").
    /// </para>
    /// <para>
    /// It lived in the SHARED TAIL rather than in the reveal path on purpose. "Go to …", the references
    /// dialog and every payload-row consumer (a task, a notification, a reminder, a search hit) come through
    /// here, so fixing the reveal alone would have left all the others reporting the short path — the same
    /// one-entrance fix that #1266 was split out of.
    /// </para>
    /// <para>
    /// The original comment justified the two crumbs as a slice simplification because "the read API doesn't
    /// expose full ancestry". That was stale: the <c>ancestors</c> rel exists and the tree already follows it.
    /// What it did NOT expose was an ADDRESS per ancestor, so a crumb built from it could be named but not
    /// clicked — fixed at the source, by having the listing advertise each ancestor's own address, rather than
    /// by composing a URL here (ADR 0543).
    /// </para>
    /// <para>
    /// <b>Falls back to the short form honestly.</b> When the folder advertises no <c>ancestors</c> rel, or the
    /// read fails, the breadcrumb says what it can rather than guessing a path — a wrong path is worse than a
    /// short one, because it looks like a fact.
    /// </para>
    /// </remarks>
    private async Task SetBreadcrumbFromAncestorsAsync(Guid folderId, string name, LinkMap? folderLinks)
    {
        var chain = new List<DocumentsClient.AncestorInfo>();
        if (_api is not null && folderLinks?.Href("ancestors") is { } ancestorsHref)
        {
            try
            {
                chain = [.. await _api.Documents.GetAncestorChainAsync(ancestorsHref)];
            }
            catch (Exception)
            {
                // Left empty: the short breadcrumb below is the honest answer when the path cannot be read.
            }
        }

        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        foreach (var ancestor in chain)
        {
            Breadcrumbs.Add(new BreadcrumbViewModel
            {
                Name = ancestor.Name,
                FolderId = ancestor.Id,
                Links = ancestor.Links,
                ShowSeparator = true,
            });
        }

        Breadcrumbs.Add(new BreadcrumbViewModel { Name = name, FolderId = folderId, Links = folderLinks, ShowSeparator = true });

        // The recycle bin lists against the repository ROOT, which is the top of the chain when there is one
        // and the folder itself when there is not (a repository opened directly).
        _currentRepositoryId = chain.Count > 0 ? chain[0].Id : folderId;
    }

    // Builds the references-dialog view model for the selected item (the view owns the dialog); the row's own
    // addresses travel with it (ADR 0555).
    public ReferencesViewModel? CreateReferencesViewModel() =>
        _api is not null && SelectedItem is { } item
            ? new ReferencesViewModel(_api, item.Id, item.Name, item.DocumentSelfHref,
                item.Links is not null && item.Links.Href("referencing-folders") is { } rf ? rf : null)
            : null;

    // Same dialog for an explicit row — the tree context menu's "References…" acts on the right-clicked folder,
    // which is not a contents-list row.
    public ReferencesViewModel? CreateReferencesViewModel(Guid itemId, string itemName, string documentSelfHref) =>
        _api is not null ? new ReferencesViewModel(_api, itemId, itemName, documentSelfHref) : null;

    // Promote a referenced folder to be the item's primary location (ADR 0506): one atomic server call, then
    // reload the tree (the item moved) and navigate to its new home. Errors surface on the status line.
    public async Task PromotePrimaryLocationAsync(string itemSelfHref, Guid itemId, Guid folderId, string folderHref)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.SetPrimaryLocationAsync(itemSelfHref, folderId);
            await ReloadTreeAsync();
            await OpenFolderAsync(folderHref, itemId);
            Status = Strings.Get("RefPrimaryLocationChanged");
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
        }
    }
}
