using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// What is SELECTED, and what that permits: the Can* gates the ribbon and the context menus bind to, the
// selection-derived names and paths (the export root, the WebDAV folder, the current folder), the export and
// import entry points, the reminder command, and the tree-context flags.
//
// One subject, and a load-bearing one: ADR 0723 makes an affordance's presence a claim about what the server
// will allow, so every gate here is the client half of that promise. Keeping them together is what lets a
// reader check the set against the row capabilities rather than hunting them through five thousand lines.
//
// It arrived under a heading reading "@-mentions (issue #383)". That heading was true of about thirty lines --
// the candidate list, the query, PickMention -- and of nothing for the three hundred after it. Fourth such
// heading found in this file (#941): inserting a member above a comment moves neither, so in a file this size
// the banners decay silently and describe whatever happened to be written first.
//
// A partial rather than a type of its own: every gate reads this view model's own observable state
// (SelectedItem, Breadcrumbs, the tree-context node), so a separate class would take the view model as a
// parameter and be a partial wearing a constructor -- the same conclusion #939, #938 and ADR 0733 reached.
public partial class MainWindowViewModel
{
    // New-folder is available only inside a folder (not at the repository-list root).
    [ObservableProperty] private bool _canCreateFolder;

    // Export (a repository/folder + subtree → .zip) is available whenever a real folder is open (ADR
    // "Repository export"); the ribbon button is additionally tenant-admin-gated in XAML.
    [ObservableProperty] private bool _canExport;


    // Rename/Delete act on the selected contents-list row; the ribbon buttons enable only when a real item is
    // picked (not a virtual archive row).
    public bool HasSelectedItem => SelectedItem is { IsArchiveEntry: false, IsArchiveBack: false };

    // Save-as is meaningful for a document, or an archive entry (both have content). Not a folder/back row.
    public bool CanSaveAs => SelectedItem is { IsFolder: false, IsArchiveBack: false };

    // "Compare versions" needs >= 2 confirmed versions to have anything to diff (ADR "Compare-versions gating +
    // default"). The count rides the listing row, so this is synchronous — the context menu, which sets the
    // selection on right-click, gets the right enabled state with no race.
    public bool CanCompareVersions => SelectedItem is { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false, VersionCount: >= 2 };

    // The approval workflow runs on a document's latest confirmed version, so "Start workflow" enables for a
    // document row (not a folder / archive row). Opened on demand in a separate window (ADR "Workflow start on
    // demand"); the window itself reports "no confirmed version" if there's nothing to run a workflow on.
    public bool CanStartWorkflow => SelectedItem is { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false };

    // Reminders (ADR "Document reminders") apply to a real document row (same guard as Start workflow).
    public bool CanRemind => SelectedItem is { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false };

    // Set by MainWindow code-behind — shows the Remind… dialog for a freshly-built ReminderDialogViewModel.
    public Func<ReminderDialogViewModel, Task>? ShowReminderDialog { get; set; }

    [RelayCommand]
    private async Task RemindAsync()
    {
        if (_api is not { } api || SelectedItem is not { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false } item || ShowReminderDialog is null)
        {
            return;
        }

        await ShowReminderDialog(new ReminderDialogViewModel(
            api, await api.Documents.RelViaSelfAsync(item.DocumentSelfHref, "reminders"), item.Name));
    }

    // The current folder's name (the export root's suggested filename) + the export call (ADR "Repository
    // export"). Returns null when there's no open folder or no session, mirroring ExportAuditBytesAsync.
    public string ExportRootName => Breadcrumbs.Count > 0 ? Breadcrumbs[^1].Name : "Repository";

    /// <summary>Where the Repositories tab is, as a path inside the WebDAV mount — empty at the root.</summary>
    /// <remarks>
    /// The mounted volume IS the tree-pane (ADR 0509), so the folder the user is looking at has a mount path,
    /// and it is the breadcrumb trail: the first crumb is the "Repositories" label rather than a folder, so it
    /// is dropped. Empty means "open the whole archive", which is what the button means with nothing selected —
    /// deliberately not a no-op, because a button that does nothing reads as broken (it was reported as such).
    /// </remarks>
    public string WebDavFolderPath()
    {
        var segments = Breadcrumbs.Skip(1).Select(b => b.Name).ToList();

        // A name carrying a slash would silently address a different folder. It cannot happen through this
        // client, but the archive is not the only thing that writes to it, so refuse rather than mis-navigate.
        return segments.Count == 0 || segments.Any(n => n.Contains('/'))
            ? string.Empty
            : string.Join('/', segments);
    }

    // The open folder's name for the import target label — null at the repository-list root (a new repository).
    public string? CurrentFolderName => _currentFolderId is null || Breadcrumbs.Count == 0 ? null : Breadcrumbs[^1].Name;

    public Task<byte[]>? ExportRepositoryBytesAsync(RepositoryArchiveClient.RepositoryExportOptions options) =>
        _currentFolderLinks is { } links && _api is { } api ? ExportRepositoryCoreAsync(api, links, options) : null;

    // The export rel lives on the document RESOURCE, not the listing row — one fetch of the folder's own
    // resource, then the follow (ADR 0559).
    private static async Task<byte[]> ExportRepositoryCoreAsync(SimplArchiveApiClient api, IReadOnlyDictionary<string, string> folderLinks, RepositoryArchiveClient.RepositoryExportOptions options) =>
        await api.RepositoryArchive.ExportRepositoryAsync(await api.Documents.RelViaSelfAsync(folderLinks["self"], "export"), options);

    // Imports an archive (ADR "Repository import") under the current folder, or as a new repository when at the
    // repository-list root, then rebuilds the tree so the imported content shows. Returns null if not signed in.
    public async Task<RepositoryArchiveClient.ImportResultInfo?> ImportAndReloadAsync(byte[] zip, bool updateExisting, bool includePermissions, bool merge, string leafConflict = "rename")
    {
        if (_api is not { } api)
        {
            return null;
        }

        // Into the open folder → its own `import` rel (resolved through its self address); at the repository
        // list root → null, and the client follows the repositories collection's import rel instead.
        var importHref = _currentFolderLinks is { } folderLinks
            ? await api.Documents.RelViaSelfAsync(folderLinks["self"], "import")
            : null;
        var result = await api.RepositoryArchive.ImportRepositoryAsync(importHref, zip, updateExisting, includePermissions, merge, leafConflict);
        await ReloadTreeAsync();
        if (_currentFolderId is { } folderId)
        {
            await LoadFolderContentsAsync(folderId);
        }

        return result;
    }

    // "Go to …" appears only for a reference row (jumps to the target's real home folder).
    public bool SelectedIsReference => SelectedItem is { IsReference: true };

    // "References …" appears only for an item that at least one reference targets.
    public bool SelectedHasReferences => SelectedItem is { HasReferences: true };

    // "Manage access …" appears for any real folder or document (not a reference/archive row) that ADVERTISED
    // the acl-entries rel. The shape check alone was the row menu's answer while the detail pane gated on a
    // server flag — one action with two answers, which is the split-surface drift ADR 0511 warns about (#858).
    // The dialog still self-gates on CanManagePermissions (ADR 0486); this stops the menu offering it first.
    public bool CanManageAccess => SelectedItem is { IsReference: false, IsArchiveEntry: false, IsArchiveBack: false }
        && SelectedItem.CanManagePermissions;

    // Rename and Delete, from what the server said about THIS row rather than from its shape (#858). A caller
    // who could merely SEE a row was offered both and learned otherwise from a 403.
    public bool CanRenameSelected => SelectedItem is { IsReference: false, IsArchiveEntry: false, IsArchiveBack: false, CanEditIndexData: true };

    public bool CanDeleteSelected => SelectedItem is { IsReference: false, IsArchiveEntry: false, IsArchiveBack: false, CanDelete: true };

    // The tree context menu's "References …" entry mirrors SelectedHasReferences, but for the RIGHT-CLICKED tree
    // node rather than the contents-list selection (ADR "Tree-pane context menu"). MainWindow sets it before the
    // menu opens.
    [ObservableProperty] private bool _treeContextHasReferences;

    // The creates the right-clicked node OFFERED, as entries rather than as one flag per kind (#673). The three
    // booleans this replaces could only ever describe masks the client knew by name; the server now sends the
    // label and the address, so a family nobody hardcoded gets a menu entry for free.
    //
    // They live under a "New" submenu because Avalonia's menu takes literal items or a bound collection and
    // never both — the flat alternative meant rebuilding all fifteen entries, separators included, as
    // view-model objects, which is a rewrite nothing could verify short of opening the menu by hand.
    [ObservableProperty] private ObservableCollection<TreeMenuEntry> _treeContextAdmits = [];

    // Whether to show the submenu at all. An empty admits list reads exactly as a missing rel does: not
    // available to you, here, now (ADR 0543) — so the entry disappears rather than opening onto nothing.
    [ObservableProperty] private bool _treeContextCanCreateAny;

    // …and "New subfolder" the same way, which it was NOT until #634. It showed unconditionally, so it appeared
    // on a notebook (which holds sections and notes), on the personal space's first level (which holds only the
    // folders it was provisioned with), on an ephemeral staging folder, and to a caller with no right to create
    // anything — each time offering an action the server refuses. The rel's absence says "not available to you,
    // here, now" (ADR 0543), which is the whole point of asking the server rather than guessing.
    [ObservableProperty] private bool _treeContextCanCreateChild;

    // The DESTRUCTIVE half, which #634 never converted (#858): Rename, Move to, Sort order and Delete showed
    // unconditionally on every tree node, so a caller who could merely SEE a folder was offered Delete on it and
    // learned otherwise from a 403 — the broken promise ADR 0543 exists to prevent, sitting three lines from the
    // create gate that already did it right.
    //
    // Rename and Sort order share CanEditIndexData because that is the one right their PUT enforces; they
    // therefore appear and disappear together, which is the truth rather than a tidy grouping.
    [ObservableProperty] private bool _treeContextCanEditIndexData;

    [ObservableProperty] private bool _treeContextCanMove;

    [ObservableProperty] private bool _treeContextCanDelete;

    // Manage access had TWO answers for one action — ungated in this menu, flag-gated in the detail pane. The
    // pane's was right (ADR 0511's split-surface drift).
    [ObservableProperty] private bool _treeContextCanManageAccess;

    // Whether the right-clicked node advertised `take-over` (ADR 0672) — only a user's personal space does, and
    // only for a caller who may perform it. Read from the NODE, so the menu describes what was clicked.
    [ObservableProperty] private bool _treeContextCanTakeOver;

    // Set while a search-hit reveal selects the parent folder's tree node after it has *already* loaded the folder
    // contents + selected the document itself (issue #340) — so the reactive load below doesn't re-fetch the folder
    // and clobber that document selection.
    private bool _suppressTreeSelectionLoad;

    async partial void OnSelectedTreeNodeChanged(TreeNodeViewModel? value)
    {
        if (_suppressTreeSelectionLoad)
        {
            return;
        }

        // The Intray / Check-out launcher nodes under Personal switch to their bottom tab (ADR "GUI-tree Personal
        // space grouping"), where the full staging / check-out UX lives.
        if (value is { IsLauncher: true })
        {
            SelectedTab = value.LauncherTab;
            return;
        }

        // The synthetic Administration/Users nodes (ADR "Tenant-admin Administration → Users view") aren't real
        // folders — selecting one only expands it; a user's personal repo node browses normally.
        if (value is { IsSynthetic: false })
        {
            SetBreadcrumbFromTreeNode(value);
            // The selected node's own address — no re-fetch to rediscover where its children live.
            await LoadFolderContentsAsync(value.Id, value.Links);
        }
    }

    /// <summary>
    /// Re-shows a tree folder's contents when it's tapped while already selected. Drilling into a subfolder
    /// via the contents list (or a breadcrumb) moves the list without moving the tree's selection, so tapping
    /// the still-selected tree node again is a no-op through the [ObservableProperty] setter (it short-circuits
    /// a same-reference re-selection, so OnSelectedTreeNodeChanged never fires and the list stays stale). This
    /// covers that gap — the code-behind Tapped handler calls it. Only the re-tap of the already-selected node
    /// reloads (a tap on a different node changes SelectedTreeNode and OnSelectedTreeNodeChanged handles it; a
    /// tap on another node's expander must not switch the list); the _currentFolderId dedup makes it a no-op
    /// when the list already shows the node.
    /// </summary>
    public async Task ReselectTreeFolderAsync(TreeNodeViewModel node)
    {
        if (node.IsSynthetic || node.IsLauncher || !ReferenceEquals(node, SelectedTreeNode) || _currentFolderId == node.Id)
        {
            return;
        }

        SetBreadcrumbFromTreeNode(node);
        await LoadFolderContentsAsync(node.Id, node.Links);
    }

    async partial void OnSelectedItemChanged(NodeViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedItem));
        OnPropertyChanged(nameof(CanSaveAs));
        OnPropertyChanged(nameof(CanCompareVersions));
        OnPropertyChanged(nameof(CanStartWorkflow));
        OnPropertyChanged(nameof(CanRemind));
        OnPropertyChanged(nameof(SelectedIsReference));
        OnPropertyChanged(nameof(SelectedHasReferences));
        OnPropertyChanged(nameof(CanManageAccess));
        OnPropertyChanged(nameof(CanRenameSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanCheckOut));
        OnPropertyChanged(nameof(CanOverrideSelected));
        OnPropertyChanged(nameof(DetailIsFolder)); // the pane's subject changed, and with it the folder-only row

        // FOLDERS TOO (#686). A folder is a Document with a mask, index fields and dates like any other, and
        // skipping it here did not leave the pane empty — it left the PREVIOUS document's values on screen
        // while the list showed a folder, which is the stale-subject condition ADR 0559 exists to prevent.
        // The web has described a selected folder since #408; this is that behaviour promoted across (ADR 0511).
        if (value is { IsArchiveEntry: false, IsArchiveBack: false })
        {
            // The tree is NOT touched here any more. Selecting a row does not move you, so the tree has
            // nothing to say about it — the mark stays on the folder you are standing in. Marking the selected
            // row made the tree answer two questions at once, and gave the mark a meaning that changed with
            // the row type: a folder row moved it, a document row cleared it. Supersedes #696's behaviour.
            await LoadDetailAsync(value);
        }
        else if (value is null)
        {
            // Nothing selected: the pane falls back to the folder the user is standing in, rather than going
            // blank. "No selection" and "just opened this folder" are the same situation and must look it.
            ClearDetail();
            await ShowOpenFolderDetailAsync();
        }
        else
        {
            // An archive row is not a document at all. Clear rather than leave the last subject standing.
            ClearDetail();
        }
    }

    /// <summary>Clears the contents-list selection — the detail pane falls back to the open folder.</summary>
    /// <remarks>
    /// Bound to Esc and to a click on the list's empty area. Before this a selection could be made but never
    /// unmade: the pane could only move from one subject to another, so the folder's own details were
    /// unreachable again without re-opening the folder.
    /// </remarks>
    [RelayCommand]
    private void ClearListSelection()
    {
        if (IsEditing)
        {
            // Esc while editing means "cancel the edit" (ADR 0550), not "change the subject".
            return;
        }

        SelectedItem = null;
    }

    /// <summary>Describes the folder the user is standing in — what the detail pane shows with nothing selected.</summary>
    private async Task ShowOpenFolderDetailAsync()
    {
        if (_currentFolderId is not { } id || _currentFolderLinks is not { } links)
        {
            return;
        }

        await LoadDetailAsync(OpenFolderMark.AsRow(id, CurrentFolderName, links, Items.Count > 0));
    }

    /// <summary>Moves the tree's "you are here" mark to the open folder (OpenFolderMark).</summary>
    private async Task MarkOpenFolderInTreeAsync()
    {
        // The selected node has to be expanded for its children to exist in the tree at all — an unexpanded
        // node has none loaded, so a folder drilled into from the list could not be found however hard we look.
        if (SelectedTreeNode is { } parent)
        {
            await parent.EnsureExpandedAsync();
        }

        if (OpenFolderMark.Move(Tree, _currentFolderId) is { } marked)
        {
            MarkedNodeChanged?.Invoke(marked);
        }
    }

    /// <summary>Raised when a node gains the mark, so the view can bring it into view (#692's desktop half).</summary>
    /// <remarks>
    /// An event rather than the view-model scrolling: only the view knows which container renders which node,
    /// and a view-model that reached for one would be doing layout.
    /// </remarks>
    public event Action<TreeNodeViewModel>? MarkedNodeChanged;
}
