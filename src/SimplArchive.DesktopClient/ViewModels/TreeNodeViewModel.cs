using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// A folder node in the left tree (folders only, like the web workbench). Children are loaded lazily the
// first time the node is expanded. See ADR "Desktop workbench UI".
public sealed partial class TreeNodeViewModel : ObservableObject
{
    private readonly Func<Guid, Task<IEnumerable<TreeNodeViewModel>>>? _loadChildren;
    private bool _loaded;

    public TreeNodeViewModel(Guid id, string name, bool hasSubfolders, Func<Guid, Task<IEnumerable<TreeNodeViewModel>>>? loadChildren, bool isReference = false, bool isPersonal = false, string? syntheticIcon = null, string? personalKind = null)
    {
        Id = id;
        Name = name;
        _loadChildren = loadChildren;
        IsReference = isReference;
        IsPersonal = isPersonal;
        SyntheticIcon = syntheticIcon;
        PersonalKind = personalKind;

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

    // The bottom-tab index the launcher activates: Inbox = 1, Check-out = 2 (ADR "Document check-out / check-in").
    public int LauncherTab => PersonalKind switch { "inbox" => 1, "checkout" => 2, _ => 0 };

    // Material Design Icons glyph — a launcher's own glyph, a synthetic admin node's own icon, a person icon for
    // the personal repository, a shortcut variant for a referenced folder, else a plain folder.
    public string IconValue => PersonalKind switch
    {
        "inbox" => "mdi-inbox-arrow-down",
        "checkout" => "mdi-lock-open-variant-outline",
        _ => SyntheticIcon ?? (IsPersonal ? "mdi-account" : IsReference ? "mdi-folder-arrow-right" : "mdi-folder"),
    };

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
}
