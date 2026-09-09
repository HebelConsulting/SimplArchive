using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The workbench's pane geometry: every resizable/collapsible pane's size and collapsed state, the carets that
// toggle them, and the load/save/reset of the persisted layout (ADR 0224 for the web's shape, 0240 for this
// client's, 0771 for the detail pane's two-way collapse).
//
// Its own partial for the reason the other partials give: MainWindowViewModel.cs is on the standing over-limit
// debt list and may only get SMALLER, so a region that grows takes a file with it rather than paying for the
// growth with a raised ceiling. Layout is a clean seam -- nothing here touches the api client, and everything
// here is two-way bound to a Grid definition or read by LayoutSettings.
public partial class MainWindowViewModel
{
    // ---- Resizable / collapsible panes (persisted, like the web client — ADR 0224/"Desktop collapsible
    // panes") ------------------------------------------------------------------------------------------

    // Two-way bound to the Grid definitions, so a GridSplitter drag updates these. Collapsing sets the size
    // to 0 (content hidden) and remembers the pre-collapse size to restore.
    [ObservableProperty] private GridLength _treeWidth;
    [ObservableProperty] private GridLength _listWidth;
    [ObservableProperty] private GridLength _indexHeight;
    [ObservableProperty] private GridLength _chatWidth;

    // The detail pane's BOTTOM HALF (preview + chat together), collapsible downward so a long mask can have
    // the whole pane. Its own row length rather than a reuse of PreviewWidth: that one collapses the preview
    // SIDEWAYS into the chat, which is a different question with a different answer.
    [ObservableProperty] private GridLength _bottomHeight;

    [ObservableProperty] private bool _treeCollapsed;
    [ObservableProperty] private bool _listCollapsed;
    [ObservableProperty] private bool _indexCollapsed;
    [ObservableProperty] private bool _chatCollapsed;
    [ObservableProperty] private bool _bottomCollapsed;

    private GridLength _treeSaved, _listSaved, _chatSaved;

    // Default pane proportions (star units) — the reset target and the load-time fallback.
    private const double DefaultTree = 1.4, DefaultList = 2, DefaultChat = 2, DefaultBottom = 3;

    /// <summary>
    /// How tall the index-data pane may grow while the preview is below it — the cap that stops a long mask
    /// pushing the preview off the bottom (ADR 0550: the preview is what the user came to look at).
    /// </summary>
    /// <remarks>
    /// Lifted while the bottom half is collapsed, because the cap's whole job is protecting the preview and
    /// there is no preview then — capping anyway would answer a collapse with grey space. It is bound to the
    /// SCROLLVIEWER, not to the row: an Auto row measures with infinite height, so a cap on the row clips
    /// instead of scrolling (see the comment in DocumentDetailPane.axaml).
    /// </remarks>
    public double IndexMaxHeight => BottomCollapsed ? double.PositiveInfinity : 280;

    /// <summary>The gutter drags only while both halves are showing — there is nothing to divide otherwise.</summary>
    public bool CanDragIndexGutter => !IndexCollapsed && !BottomCollapsed;

    // Caret glyph for each gutter's collapse toggle (points the way it collapses; flips when collapsed).
    public string TreeCaret => TreeCollapsed ? "mdi-chevron-right" : "mdi-chevron-left";
    public string ListCaret => ListCollapsed ? "mdi-chevron-right" : "mdi-chevron-left";
    public string IndexCaret => IndexCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";
    public string ChatCaret => ChatCollapsed ? "mdi-chevron-left" : "mdi-chevron-right";
    public string BottomCaret => BottomCollapsed ? "mdi-chevron-up" : "mdi-chevron-down";

    partial void OnTreeCollapsedChanged(bool value) => OnPropertyChanged(nameof(TreeCaret));
    partial void OnListCollapsedChanged(bool value) => OnPropertyChanged(nameof(ListCaret));
    partial void OnChatCollapsedChanged(bool value) => OnPropertyChanged(nameof(ChatCaret));

    // Both of these gate more than their own caret — the gutter's draggability, and (for the bottom) the cap
    // on the index pane's height. A computed property is only as live as the notification behind it, and one
    // raised from the wrong hook is an affordance that never refreshes.
    partial void OnIndexCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(IndexCaret));
        OnPropertyChanged(nameof(CanDragIndexGutter));
    }

    partial void OnBottomCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(BottomCaret));
        OnPropertyChanged(nameof(CanDragIndexGutter));
        OnPropertyChanged(nameof(IndexMaxHeight));
    }

    [RelayCommand]
    private void ToggleTree()
    {
        if (TreeCollapsed) { TreeWidth = _treeSaved; TreeCollapsed = false; }
        else { _treeSaved = TreeWidth; TreeWidth = new GridLength(0); TreeCollapsed = true; }
        SaveLayout();
    }

    [RelayCommand]
    private void ToggleList()
    {
        if (ListCollapsed) { ListWidth = _listSaved; ListCollapsed = false; }
        else { _listSaved = ListWidth; ListWidth = new GridLength(0); ListCollapsed = true; }
        SaveLayout();
    }

    [RelayCommand]
    private void ToggleIndex()
    {
        // Expands to Auto, never to a remembered height — unlike every other pane here. A drag of this pane is a
        // PEEK (ADR 0550), so there is nothing to remember: restoring a saved height would let one drag survive a
        // collapse/expand cycle, and (via SaveLayout) the whole session after it. That is the same leak the web
        // client had through localStorage (issue #413), just by a different route.
        if (IndexCollapsed) { IndexHeight = GridLength.Auto; IndexCollapsed = false; }
        else { IndexHeight = new GridLength(0); IndexCollapsed = true; ExpandBottom(); }
        SaveLayout();
    }

    /// <summary>
    /// Collapses the detail pane's bottom half — preview AND chat — downward, so the index data has the whole
    /// pane. The mirror of <see cref="ToggleIndex"/>, and the second caret on the same gutter.
    /// </summary>
    /// <remarks>
    /// Collapsing puts the index row on a STAR height rather than leaving it Auto: Auto would fit the content
    /// and leave the reclaimed space grey, which is the opposite of what collapsing was asked for. Expanding
    /// returns it to Auto — the resting state this pane has had since ADR 0550, and deliberately not a
    /// remembered height (a drag here is a peek that must not survive the cycle, issue #413).
    /// </remarks>
    [RelayCommand]
    private void ToggleBottom()
    {
        if (BottomCollapsed)
        {
            ExpandBottom();
        }
        else
        {
            BottomHeight = new GridLength(0);
            BottomCollapsed = true;
            IndexHeight = new GridLength(1, GridUnitType.Star);
            // The two halves cannot both be gone, so collapsing one expands the other.
            IndexCollapsed = false;
        }

        SaveLayout();
    }

    private void ExpandBottom()
    {
        BottomHeight = new GridLength(DefaultBottom, GridUnitType.Star);
        BottomCollapsed = false;
        if (!IndexCollapsed)
        {
            IndexHeight = GridLength.Auto;
        }
    }

    [RelayCommand]
    private void ToggleChat()
    {
        if (ChatCollapsed) { ChatWidth = _chatSaved; ChatCollapsed = false; }
        else { _chatSaved = ChatWidth; ChatWidth = new GridLength(0); ChatCollapsed = true; }
        SaveLayout();
    }

    // Restores the default pane proportions and expands every pane — an escape hatch when the persisted
    // layout has drifted into an inconsistent state (GridSplitter drags can mix star and absolute sizes).
    [RelayCommand]
    private void ResetLayout()
    {
        _treeSaved = new GridLength(DefaultTree, GridUnitType.Star);
        _listSaved = new GridLength(DefaultList, GridUnitType.Star);
        _chatSaved = new GridLength(DefaultChat, GridUnitType.Star);

        TreeCollapsed = ListCollapsed = IndexCollapsed = ChatCollapsed = BottomCollapsed = false;

        TreeWidth = _treeSaved;
        ListWidth = _listSaved;
        IndexHeight = GridLength.Auto; // fits its content — there is no default proportion to restore
        ChatWidth = _chatSaved;
        BottomHeight = new GridLength(DefaultBottom, GridUnitType.Star);

        Intray.ResetLayout();
        ResetPreviewLayout();

        StoredColNameWidth = DefaultColName;
        ColTypeWidth = DefaultColType;
        ColDateWidth = DefaultColDate;
        ColSizeWidth = DefaultColSize;
        ColTagsWidth = DefaultColTags;
        ColOwnerWidth = DefaultColOwner;

        SaveLayout();
        Status = Strings.Get("StLayoutReset");
    }

    private void LoadLayout()
    {
        var settings = LayoutSettingsStore.Load();
        _treeSaved = GridLengths.ParseOrStar(settings.TreeWidth, DefaultTree);
        _listSaved = GridLengths.ParseOrStar(settings.ListWidth, DefaultList);
        _chatSaved = GridLengths.ParseOrStar(settings.ChatWidth, DefaultChat);

        TreeCollapsed = settings.TreeCollapsed;
        ListCollapsed = settings.ListCollapsed;
        IndexCollapsed = settings.IndexCollapsed;
        ChatCollapsed = settings.ChatCollapsed;
        BottomCollapsed = settings.BottomCollapsed;
        BottomHeight = BottomCollapsed
            ? new GridLength(0)
            : new GridLength(DefaultBottom, GridUnitType.Star);

        TreeWidth = TreeCollapsed ? new GridLength(0) : _treeSaved;
        ListWidth = ListCollapsed ? new GridLength(0) : _listSaved;
        // Auto, not a persisted value: this pane fits its content (ADR 0550), and a stored height would be the
        // height of whatever happened to be selected when it was last dragged. Nothing reads a saved height for
        // this pane any more — the collapse toggle expands to Auto too.
        // …except when the bottom half is collapsed, where Auto would fit the content and leave the reclaimed
        // space grey — the one state in which this pane is meant to fill what it is given.
        IndexHeight = (IndexCollapsed, BottomCollapsed) switch
        {
            (true, _) => new GridLength(0),
            (false, true) => new GridLength(1, GridUnitType.Star),
            _ => GridLength.Auto,
        };
        ChatWidth = ChatCollapsed ? new GridLength(0) : _chatSaved;

        Intray.LoadLayout(settings);
        LoadPreviewLayout(settings);

        StoredColNameWidth = ParseDouble(settings.ColName, DefaultColName);
        ColTypeWidth = ParseDouble(settings.ColType, DefaultColType);
        ColDateWidth = ParseDouble(settings.ColDate, DefaultColDate);
        ColSizeWidth = ParseDouble(settings.ColSize, DefaultColSize);
        ColTagsWidth = ParseDouble(settings.ColTags, DefaultColTags);
        ColOwnerWidth = ParseDouble(settings.ColOwner, DefaultColOwner);
    }

    // Persists the current sizes + collapsed state. Called on each toggle and when the window closes (to
    // capture GridSplitter drag-resizes).
    public void SaveLayout()
    {
        var settings = new LayoutSettings
        {
            TreeWidth = (TreeCollapsed ? _treeSaved : TreeWidth).ToString(),
            ListWidth = (ListCollapsed ? _listSaved : ListWidth).ToString(),
            // Always "Auto": a peek must not reach the settings file (issue #413). The field stays in the
            // settings shape so an older file still loads; its value is simply never meaningful now.
            IndexHeight = GridLength.Auto.ToString(),
            ChatWidth = (ChatCollapsed ? _chatSaved : ChatWidth).ToString(),
            TreeCollapsed = TreeCollapsed,
            ListCollapsed = ListCollapsed,
            IndexCollapsed = IndexCollapsed,
            ChatCollapsed = ChatCollapsed,
            // Only the collapsed FLAG, no height: the bottom half restores to its default proportion, the same
            // reasoning as IndexHeight above — a dragged split is this session's, not a preference.
            BottomCollapsed = BottomCollapsed,
            // The STORED width, not the drawn one: persisting the computed value would bake one pane width
            // into the layout file and make the next session open with a Name column sized for the last
            // session's window (#786).
            ColName = StoredColNameWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColType = ColTypeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColDate = ColDateWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColSize = ColSizeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColTags = ColTagsWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColOwner = ColOwnerWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        Intray.WriteLayout(settings);   // the tab's four panes are its own to describe
        WritePreviewLayout(settings);
        LayoutSettingsStore.Save(settings);
    }

    // ---- The Intray tab's four collapsible/resizable panes (ADR "Collapsible inbox panes") -------------
    // Same mechanism as the Repositories panes above — each pane's body row height is two-way bound, collapse
    // sets it to 0, and a header caret toggles it — but the state lives on IntrayTabViewModel, which the
    // load/save/reset above delegate to. The note is here rather than beside that code because THIS is where
    // the two halves of the layout meet.

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}
