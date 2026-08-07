using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// A folder node in the left tree (folders only, like the web workbench). Children are loaded lazily the
// first time the node is expanded. See ADR "Desktop workbench UI".
public sealed partial class TreeNodeViewModel : ObservableObject
{
    private readonly Func<Guid, Task<IEnumerable<TreeNodeViewModel>>>? _loadChildren;
    private bool _loaded;

    public TreeNodeViewModel(Guid id, string name, bool hasSubfolders, Func<Guid, Task<IEnumerable<TreeNodeViewModel>>>? loadChildren, bool isReference = false, bool isPersonal = false, string? syntheticIcon = null, string? personalKind = null, bool hasReferences = false, bool hasChildren = true)
    {
        Id = id;
        Name = name;
        _loadChildren = loadChildren;
        IsReference = isReference;
        IsPersonal = isPersonal;
        SyntheticIcon = syntheticIcon;
        PersonalKind = personalKind;
        HasReferences = hasReferences;
        HasChildren = hasChildren;

        // A placeholder child makes the expander appear before the real children are loaded.
        if (hasSubfolders)
        {
            Children.Add(Placeholder);
        }
    }

    private static readonly TreeNodeViewModel Placeholder = new(Guid.Empty, "…", false, null);

    // For a referenced folder, Id is the TARGET folder's id — so expanding loads the target's children,
    // selecting loads its contents, and a drop files into it, all through the existing Id paths. See ADR
    // "Referenced folder in the tree".
    public Guid Id { get; }

    public string Name { get; }

    public bool IsReference { get; }

    // The user's personal repository, pinned at the top of the tree (ADR "Per-user personal repository").
    public bool IsPersonal { get; }

    // Non-null for a synthetic tenant-admin node (ADR "Tenant-admin Administration → Users view") — it isn't a
    // real folder, so selecting it does nothing (it only expands).
    public string? SyntheticIcon { get; }

    public bool IsSynthetic => SyntheticIcon is not null;

    // Non-null ("inbox" / "checkout") for the launcher nodes nested under Personal (ADR "GUI-tree Personal space
    // grouping") — selecting one switches to the matching bottom tab instead of loading folder contents.
    public string? PersonalKind { get; }

    public bool IsLauncher => PersonalKind is not null;

    // At least one reference (shortcut) points at this folder — gates the tree context menu's "References…"
    // entry, exactly as SelectedHasReferences gates the contents-list one.
    public bool HasReferences { get; }

    // The bottom-tab index the launcher activates: Inbox = 1, Check-out = 2 (ADR "Document check-out / check-in").
    public int LauncherTab => PersonalKind switch { "inbox" => 1, "checkout" => 2, _ => 0 };

    // Whether this folder holds ANYTHING — a document, a subfolder, or a reference filed into it (issue #376).
    // Defaults to true so the pseudo-nodes (Administration, the Inbox / Check-out launchers, the demo/screenshot
    // stubs) never render as "empty".
    public bool HasChildren { get; }

    // An EMPTY folder — nothing at all inside (ADR "Empty-folder tree icon", issue #352). Note this is NOT the
    // same as "no subfolders": a folder holding only documents is a leaf in the folders-only tree but is not
    // empty. The caller's OWN Personal root is never empty (it always holds the Inbox / Check-out launchers) so
    // it's constructed with the default hasChildren: true; an admin-browsed other user's personal repo has no
    // launchers and passes its real flag.
    public bool IsEmptyFolder => !HasChildren && !IsSynthetic && !IsLauncher;

    // Material Design Icons glyph — a launcher's own glyph, a synthetic admin node's own icon, a person icon for
    // the personal repository, a shortcut variant for a referenced folder, else a plain folder.
    //
    // An empty one takes the OUTLINE variant of whatever it is, so "nothing here" is carried by the glyph's shape
    // and not by colour alone — it stays readable to someone who can't distinguish the two golds, and at any
    // contrast setting. Appending the suffix rather than listing the outline names keeps the two halves from
    // drifting apart; every glyph reachable here has one, and the pseudo-nodes never qualify as empty.
    public string IconValue => IsEmptyFolder ? $"{BaseIconValue}-outline" : BaseIconValue;

    private string BaseIconValue => PersonalKind switch
    {
        "inbox" => "mdi-inbox-arrow-down",
        "checkout" => "mdi-lock-open-variant-outline",
        _ => SyntheticIcon ?? (IsPersonal ? "mdi-account" : IsReference ? "mdi-folder-arrow-right" : "mdi-folder"),
    };

    // Which of App.axaml's theme brushes paints this glyph (ADR "Folder icon scheme"). Gold marks a place
    // documents live — a folder, the personal root, a referenced folder. The Inbox / Check-out launchers and the
    // synthetic admin nodes are not containers and take the muted text colour, so the gold means something
    // rather than merely decorating every row.
    //
    // An empty folder is the SAME gold at reduced alpha, which is what lets it recede in BOTH themes; the old
    // fixed pale yellow could only do that on light, and on dark actually out-shouted the gold.
    //
    // Named rather than resolved to an IBrush here: the brush has to come from the ACTIVE theme dictionary, and
    // a value the view model resolves once would keep the startup theme after the OS switched. The view binds
    // this to style classes, so the DynamicResource lookup happens where it can follow the theme.
    public string IconBrushKey => IsEmptyFolder ? "WbFolderEmpty"
        : IsLauncher || IsSynthetic ? "WbMuted"
        : "WbFolder";

    // The three are mutually exclusive — one style class each, since Avalonia can't bind a resource KEY.
    public bool UsesFolderBrush => IconBrushKey == "WbFolder";

    public bool UsesEmptyFolderBrush => IconBrushKey == "WbFolderEmpty";

    public bool UsesMutedBrush => IconBrushKey == "WbMuted";

    // Set when this node is loaded as a child (used to build the breadcrumb path from a tree selection).
    public TreeNodeViewModel? Parent { get; private set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    async partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded || _loadChildren is null)
        {
            return;
        }

        _loaded = true;
        Children.Clear();
        foreach (var child in await _loadChildren(Id))
        {
            child.Parent = this;
            Children.Add(child);
        }
    }

    // Expands this node, loading its children first if not already loaded — awaitable (unlike the IsExpanded
    // setter's fire-and-forget handler), so a caller revealing a deep path can walk it level by level and know the
    // children are present before descending (issue #340).
    public async Task EnsureExpandedAsync()
    {
        if (!_loaded && _loadChildren is not null)
        {
            await ReloadChildrenAsync(); // loads children + sets IsExpanded (and marks _loaded so nothing double-loads)
        }
        else
        {
            IsExpanded = true;
        }
    }

    // Re-fetch this node's children in place and keep it expanded — used after a structural change under this
    // folder (e.g. a new subfolder) so the tree reflects it WITHOUT a full rebuild that would collapse everything
    // (ADR "Keep the tree expanded on a structural change"). No-op for a node that can't have children.
    public async Task ReloadChildrenAsync()
    {
        if (_loadChildren is null)
        {
            return;
        }

        _loaded = true; // mark loaded so re-expanding doesn't double-load via OnIsExpandedChanged
        Children.Clear();
        foreach (var child in await _loadChildren(Id))
        {
            child.Parent = this;
            Children.Add(child);
        }
        IsExpanded = true;
    }
}
