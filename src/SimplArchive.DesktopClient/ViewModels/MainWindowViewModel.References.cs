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
            await OpenFolderAsync(
                node.Links?.GetValueOrDefault("go-to")
                ?? throw new InvalidOperationException($"The shortcut '{node.Name}' advertised no 'go-to' rel (ADR 0543/0555)."),
                node.Id);
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
    private async Task OpenLoadedFolderAsync(Guid folderId, string name, IReadOnlyDictionary<string, string>? folderLinks, Guid? selectTargetId)
    {
        await LoadFolderContentsAsync(folderId, folderLinks);
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = name, FolderId = folderId, ShowSeparator = true });
        if (selectTargetId is { } targetId)
        {
            // Prefer the item's real row; fall back to its reference (shortcut) row when the folder holds only
            // a shortcut (a referencing folder) — selecting a reference loads the target document for viewing.
            SelectedItem = Items.FirstOrDefault(i => i.Id == targetId && !i.IsReference)
                ?? Items.FirstOrDefault(i => i.Id == targetId);
        }
    }

    // Builds the references-dialog view model for the selected item (the view owns the dialog); the row's own
    // addresses travel with it (ADR 0555).
    public ReferencesViewModel? CreateReferencesViewModel() =>
        _api is not null && SelectedItem is { } item
            ? new ReferencesViewModel(_api, item.Id, item.Name, item.DocumentSelfHref,
                item.Links is not null && item.Links.TryGetValue("referencing-folders", out var rf) ? rf : null)
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
