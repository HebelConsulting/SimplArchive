using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// A folder node in the left tree (folders only, like the web workbench). Children are loaded lazily the
// first time the node is expanded. See ADR "Desktop workbench UI".
public sealed partial class TreeNodeViewModel : ObservableObject
{
    // Takes the NODE, not its id (ADR 0543, issue #416). A node knows its own addresses; passing an id forced
    // the loader to rebuild `api/documents/{id}/children` from a template, which is how an id-shaped tree model
    // makes every consumer compose URLs.
    private readonly Func<TreeNodeViewModel, Task<IEnumerable<TreeNodeViewModel>>>? _loadChildren;
    private bool _loaded;

    public TreeNodeViewModel(Guid id, string name, bool hasSubfolders, Func<TreeNodeViewModel, Task<IEnumerable<TreeNodeViewModel>>>? loadChildren, bool isReference = false, bool isPersonal = false, string? syntheticIcon = null, string? personalKind = null, bool hasReferences = false, bool hasChildren = true, IReadOnlyDictionary<string, string>? links = null, IReadOnlyList<Services.CreatableChild>? admits = null, string? icon = null,
        bool canDelete = false, bool canEditIndexData = false, bool canMove = false, bool canManagePermissions = false)
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
        Links = links;
        Admits = admits ?? [];
        CanDelete = canDelete;
        CanEditIndexData = canEditIndexData;
        CanMove = canMove;
        CanManagePermissions = canManagePermissions;
        MaskIconToken = icon;

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
    // The addresses the server advertised for this node, as the listing carried them (ADR 0543). Null for the
    // SYNTHETIC rows — Administration, the personal-space groupings, the placeholder — which stand for no server
    // resource at all, so there is nothing to follow and Href() correctly refuses.
    public IReadOnlyDictionary<string, string>? Links { get; }

    /// <summary>
    /// The kinds of child this node will accept, with the address that creates each (#673).
    /// </summary>
    /// <remarks>
    /// Read from the RIGHT-CLICKED node rather than from pane state, for the reason ADR 0559 gives: the pane
    /// describes whatever last finished loading, which during a load is a different folder than the one under
    /// the cursor. Empty for a synthetic row, and for a folder that accepts nothing — the same reading as a
    /// missing rel, so the menu shows no creates rather than offering ones the server refuses.
    /// </remarks>
    public IReadOnlyList<Services.CreatableChild> Admits { get; }

    /// <summary>The mask's icon token as the server sent it, or null to keep the generic glyph.</summary>
    public string? MaskIconToken { get; }

    /// <summary>
    /// Whether the server advertised <paramref name="rel"/> for this node — i.e. whether the affordance it
    /// reaches exists here at all. Ask this before offering the action; a missing rel means "not available to
    /// you, here, now" (ADR 0543), and <see cref="Href"/> deliberately throws rather than answering it.
    /// </summary>
    /// <summary>What the server said this caller may do to THIS node (#858).</summary>
    /// <remarks>
    /// False on a synthetic node (Administration, the Personal launchers) and on demo/harness nodes, which is
    /// correct rather than incidental: those are not documents and have nothing to rename, move or delete.
    /// </remarks>
    public bool CanDelete { get; }

    public bool CanEditIndexData { get; }

    public bool CanMove { get; }

    public bool CanManagePermissions { get; }

    public bool HasRel(string rel) => Links is not null && Links.ContainsKey(rel);

    /// <summary>The advertised href for <paramref name="rel"/>; throws rather than composing one.</summary>
    public string Href(string rel) =>
        Links is not null && Links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException(
                $"The '{rel}' rel was not advertised for tree node '{Name}'. Follow a rel the resource offers, or "
                + "fetch the resource — do not compose the URL (ADR 0543).");

    /// <summary>
    /// The DOCUMENT resource's own address. A repository row calls its document view <c>document</c> — its
    /// <c>self</c> is the repository view (ADR 0200) — while every other row's <c>self</c> IS the document.
    /// </summary>
    public string DocumentSelfHref =>
        Links is not null && Links.TryGetValue("document", out var doc) ? doc : Href("self");

    public Guid Id { get; }

    public string Name { get; }

    public bool IsReference { get; }

    // The user's personal repository, pinned at the top of the tree (ADR "Per-user personal repository").
    public bool IsPersonal { get; }

    // Non-null for a synthetic tenant-admin node (ADR "Tenant-admin Administration → Users view") — it isn't a
    // real folder, so selecting it does nothing (it only expands).
    public string? SyntheticIcon { get; }

    public bool IsSynthetic => SyntheticIcon is not null;

    // Non-null ("intray" / "checkout") for the launcher nodes nested under Personal (ADR "GUI-tree Personal space
    // grouping") — selecting one switches to the matching bottom tab instead of loading folder contents.
    public string? PersonalKind { get; }

    public bool IsLauncher => PersonalKind is not null;

    // At least one reference (shortcut) points at this folder — gates the tree context menu's "References…"
    // entry, exactly as SelectedHasReferences gates the contents-list one.
    public bool HasReferences { get; }

    // The bottom-tab index the launcher activates: Intray = 1, Check-out = 2 (ADR "Document check-out / check-in").
    public int LauncherTab => PersonalKind switch { "intray" => 1, "checkout" => 2, _ => 0 };

    // Whether this folder holds ANYTHING — a document, a subfolder, or a reference filed into it (issue #376).
    // Defaults to true so the pseudo-nodes (Administration, the Intray / Check-out launchers, the demo/screenshot
    // stubs) never render as "empty".
    public bool HasChildren { get; }

    // An EMPTY folder — nothing at all inside (ADR "Empty-folder tree icon", issue #352). Note this is NOT the
    // same as "no subfolders": a folder holding only documents is a leaf in the folders-only tree but is not
    // empty. The caller's OWN Personal root is never empty (it always holds the Intray / Check-out launchers) so
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
        "intray" => "mdi-inbox-arrow-down",
        "checkout" => "mdi-lock-open-variant-outline",
        // A REFERENCE wins over the mask: that this row is a shortcut is the more important thing to say, and
        // the target's own row shows the mask glyph anyway. A synthetic node has no mask at all.
        _ => SyntheticIcon
            ?? (IsReference ? "mdi-folder-arrow-right"
                : Services.MaskIcon.For(MaskIconToken) ?? (IsPersonal ? "mdi-account" : "mdi-folder")),
    };

    // Which of App.axaml's theme brushes paints this glyph (ADR "Folder icon scheme"). Gold marks a place
    // documents live — a folder, the personal root, a referenced folder. The Intray / Check-out launchers and the
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

    /// <summary>Whether this node is the SELECTED subject — which is not the same as the OPEN folder (#696).</summary>
    /// <remarks>
    /// Separate from the TreeView's own <c>SelectedItem</c> (bound to <c>SelectedTreeNode</c>), because on this
    /// client the selected tree node IS the navigation: setting it loads that folder's contents. Marking a node
    /// without navigating to it therefore needs its own state, exactly as the web needed a class it applies
    /// from the shell's selection rather than from the tree's own selection (ADR 0661).
    /// </remarks>
    [ObservableProperty]
    private bool _isMarked;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Told whenever this node opens or closes, so the shape can be remembered between sessions.</summary>
    /// <remarks>
    /// Handed DOWN to children as they load rather than wired per node by whoever constructs them: children
    /// appear lazily and in several places, and a callback attached at each construction site is one that a new
    /// site forgets. Set it on the roots and every descendant inherits it.
    /// </remarks>
    public Action<TreeNodeViewModel, bool>? ExpansionChanged { get; set; }

    async partial void OnIsExpandedChanged(bool value)
    {
        // Reported for CLOSING as well as opening — a collapse is a change to the remembered shape, and the
        // early return below only concerns loading children.
        if (!IsSynthetic && !IsLauncher)
        {
            ExpansionChanged?.Invoke(this, value);
        }

        if (!value || _loaded || _loadChildren is null)
        {
            return;
        }

        _loaded = true;
        Children.Clear();
        foreach (var child in await _loadChildren(this))
        {
            child.Parent = this;
            child.ExpansionChanged = ExpansionChanged;
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
        foreach (var child in await _loadChildren(this))
        {
            child.Parent = this;
            child.ExpansionChanged = ExpansionChanged; // the second place children appear; both must pass it on
            Children.Add(child);
        }
        IsExpanded = true;
    }
}
