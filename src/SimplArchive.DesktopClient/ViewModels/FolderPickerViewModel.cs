using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

// The Repositories tab's currently-selected document, offered as extra filing targets (ADR "Context-aware
// inbox filing dialog"): file as a new version of it, or into the folder that contains it. Paths are the
// breadcrumb trails shown in the dialog.
// DocumentLinks/FolderLinks carry the rows' advertised addresses (ADR 0555) so the filing consumer follows
// them instead of composing anything from the ids beside them (#443).
public sealed record DocumentFilingContext(
    Guid DocumentId, string DocumentName, string DocumentPath, Guid FolderId, string FolderName, string FolderPath,
    IReadOnlyDictionary<string, string>? DocumentLinks = null, IReadOnlyDictionary<string, string>? FolderLinks = null);

public enum FilingMode { AsVersion, InFolder, PickedFolder }

// The dialog's outcome: file the item as a new version of TargetId (AsVersion) or into folder TargetId, plus
// an optional feed comment (ADR "Filing posts a feed comment"). TargetLinks is the chosen row's own advertised
// address set (ADR 0555) — the consumer follows it (`versions`, `children`, or `self` to resolve one).
public sealed record FilingResult(FilingMode Mode, Guid TargetId, string? Comment, IReadOnlyDictionary<string, string>? TargetLinks = null);

// Backs the folder-picker dialog (ADR "S3-backed inbox", phase 2): a folders-only tree of repositories,
// lazily loaded like the main workbench tree, for choosing where to file an intray item. When a document is
// selected on the Repositories tab (ADR "Context-aware inbox filing dialog"), it also offers filing as a new
// version of that document or into its folder, chosen by radio buttons.
public sealed partial class FolderPickerViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;
    private readonly DocumentFilingContext? _context;

    // bulk = filing multiple items at once (ADR "Bulk-file multiple inbox items") — no "as new version" option
    // (that's single-item only), just file-in-folder / pick.
    public FolderPickerViewModel(SimplArchiveApiClient api, DocumentFilingContext? context = null, bool bulk = false)
    {
        _api = api;
        _context = context;

        // "As version" only when a real document is selected and this isn't a bulk file; "in folder" whenever a
        // context (a folder target) is present.
        ShowAsVersion = context is { DocumentId: var d } && d != Guid.Empty && !bulk;
        ShowInFolder = context is not null;
        HasOptions = ShowAsVersion || ShowInFolder;

        if (ShowAsVersion) { _modeAsVersion = true; }
        else if (ShowInFolder) { _modeInFolder = true; }
        else { _modePicked = true; }
    }

    public bool ShowAsVersion { get; }
    public bool ShowInFolder { get; }
    public bool HasOptions { get; }

    public string DocumentName => _context?.DocumentName ?? "";
    public string DocumentPath => _context?.DocumentPath ?? "";
    public string FolderName => _context?.FolderName ?? "";
    public string FolderPath => _context?.FolderPath ?? "";

    // Radio selection — one of three modes (a shared GroupName keeps them mutually exclusive).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanCommit))] private bool _modeAsVersion;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanCommit))] private bool _modeInFolder;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanCommit))] private bool _modePicked;

    // Optional feed comment posted on the filed document(s) (ADR "Filing posts a feed comment").
    [ObservableProperty] private string _comment = string.Empty;

    // The chosen target, or null if nothing valid is selected (e.g. "choose a folder" with no folder picked).
    public FilingResult? BuildResult()
    {
        var comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim();

        if (ModeAsVersion && _context is not null)
        {
            return new FilingResult(FilingMode.AsVersion, _context.DocumentId, comment, _context.DocumentLinks);
        }

        if (ModeInFolder && _context is not null)
        {
            return new FilingResult(FilingMode.InFolder, _context.FolderId, comment, _context.FolderLinks);
        }

        // A personal space's ROOT is shown and expands, but is not a target: its first level is provisioned,
        // not user-filled (#634), so the server would refuse. Returning null here is what leaves the dialog's
        // commit disabled rather than offering a click that cannot succeed (ADR 0543's spirit).
        //
        // The same reasoning extends to the create capability (#873, now a flag — #854).
        // A folder that admits no plain child — a notebook, which holds sections and notes — would refuse the
        // filing, and the picker offered it anyway. ADR 0689 says what is SHOWN and what is CHOOSABLE are
        // separate questions; this is the choosable half, asked of the same value the dialog commits with so
        // the button and the outcome cannot disagree.
        return SelectedNode is { IsPersonal: false, CanCreateChildren: true } node
            ? new FilingResult(FilingMode.PickedFolder, node.Id, comment, node.Links)
            : null;
    }

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];

    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanCommit))] private TreeNodeViewModel? _selectedNode;

    /// <summary>Whether the dialog has a target to commit — asked of <see cref="BuildResult"/> so the button
    /// and the outcome cannot disagree about what counts as one.</summary>
    public bool CanCommit => BuildResult() is not null;

    public async Task LoadAsync()
    {
        Roots.Clear();

        // The SAME roots the workbench tree shows (ADR 0689): the personal space is excluded from
        // GET /repositories on purpose — it is fetched from the `me` resource — so a picker built on that
        // listing alone silently omitted the one place a person is most likely to be filing into.
        var personal = await _api.Profile.GetPersonalRepositoryAsync();
        var shared = await _api.Documents.GetRepositoriesAsync();

        foreach (var root in FilingRoots.Compose(personal, shared, r => r.Name))
        {
            // Always expandable for the personal space: what a user wants is one of the folders inside it, and
            // it holds at least the provisioned ones even when HasSubfolders has not caught up.
            Roots.Add(new TreeNodeViewModel(
                root.Node.Id, root.Node.Name, root.Node.HasSubfolders || !root.Selectable, LoadChildrenAsync,
                isPersonal: !root.Selectable, links: root.Node.Links, canCreateChildren: root.Node.CanCreateChildren));
        }
    }

    private async Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync(TreeNodeViewModel node)
    {
        var children = await _api.Documents.GetChildrenAsync(node.Href("children"));
        return children
            .Where(c => !c.HasVersions) // folders only
                                        // canCreateChildren must be carried explicitly (#854). While it was the `create-child` REL it rode
                                        // inside Links, which this site already passed, so the gate worked here without anyone thinking
                                        // about it. A flag does not ride along — it is a field, and a construction site that omits it says
                                        // "you may not create here" in the safe direction but the wrong one. This picker's two sites are
                                        // its own, separate from the tree's, and they are exactly the ones the conversion first missed.
            .Select(c => new TreeNodeViewModel(
                c.Id, c.Name, c.HasSubfolders, LoadChildrenAsync, links: c.Links, canCreateChildren: c.CanCreateChildren));
    }
}
