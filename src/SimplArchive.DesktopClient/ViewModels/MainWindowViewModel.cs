using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Theming;

namespace SimplArchive.DesktopClient.ViewModels;

// The desktop workbench — mirrors the web Repositories tab: bottom tabs, a ribbon, and the
// tree │ contents-list │ (index-data over (preview │ chat)) panes. See ADR "Desktop workbench UI".
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly OidcLoopbackAuthenticator _authenticator = new();
    private SimplArchiveApiClient? _api;
    private Guid? _currentFolderId;
    private Guid? _currentRepositoryId;
    private Guid? _selectedDocumentId;

    // The .zip document whose entries the contents list is currently showing (ADR "Zip file browsing"), or
    // null when not browsing an archive.
    private Guid? _archiveDocumentId;

    // ---- Session / tabs -------------------------------------------------------------------------------

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _userEmail = "";
    [ObservableProperty] private string _status = "Not logged in.";

    // The bottom TabControl's selected index (0 = Repositories) — bound so opening a search result can switch
    // back to the workbench.
    [ObservableProperty] private int _selectedTab;

    // The bottom tabs (Repositories/Intray/Tasks/Search) are a TabControl in the view; only Repositories has
    // content in this slice.

    // The Recycle bin tab (ADR "Desktop recycle bin parity") — its own master-detail VM with an INDEPENDENT
    // preview (RecycleBin.Preview), so a deleted document's preview never entangles the Repositories/Intray one.
    public RecycleBinTabViewModel RecycleBin { get; } = new();

    // The Check-out tab (ADR "Document check-out / check-in") — the caller's checked-out documents + their
    // local working-copy status.
    public CheckoutTabViewModel Checkout { get; } = new();

    // The Contacts and Calendar tabs (#564) — the caller's addressbooks and calendars. Their own VMs, like
    // Check-out above: a tab's worth of state belongs to the tab, and this file is far over the 1000-line
    // ceiling. Treated as ONE surface in review (ADR 0511), so they are declared together.
    public ContactsTabViewModel ContactsTab { get; } = new();

    public CalendarTabViewModel CalendarTab { get; } = new();

    // The environment strip (#501) — set from the chosen server profile at login, empty for the normal case.
    public EnvironmentBannerViewModel EnvBanner { get; } = new();

    public MainWindowViewModel()
    {
        LoadLayout();
        Preview.StatusReporter = m => Status = m;
        IntrayPreview.StatusReporter = m => Status = m;
        SearchPreview.StatusReporter = m => Status = m;
        RecycleBin.StatusReporter = m => Status = m;
        Checkout.StatusReporter = m => Status = m;
        Checkout.OnChanged = RefreshAfterCheckoutChangeAsync;
        ContactsTab.StatusReporter = m => Status = m;
        CalendarTab.StatusReporter = m => Status = m;
        IntrayActions.Connect(() => _api, RefreshIntrayAsync, m => Status = m, () => _currentUserId);
    }

    // What a user can do TO a staged intray item — send it on, claim it, delete it, and take its pages apart or
    // together (#487). Extracted from this class rather than added to it: the actions are one cohesive thing,
    // and this file is over the 1000-line ceiling, so growing it further is not on the table (ADR 0575).
    public IntrayItemActionsViewModel IntrayActions { get; } = new();

    // Sets the authenticated api client for the whole workbench, including both preview surfaces + the Recycle
    // bin tab (so every surface shares the same session token).
    private void UseApi(SimplArchiveApiClient api)
    {
        _api = api;
        Preview.Api = api;
        IntrayPreview.Api = api;
        SearchPreview.Api = api;
        RecycleBin.SetApi(api);
        ContactsTab.Setup(api);
        CalendarTab.Setup(api);

        // The straightening toggle's state belongs to the USER, not the machine, so it is read from the server
        // once per session rather than restored from local settings (#491).
        Safe.Fire(async () => await IntrayActions.LoadIngestPreferencesAsync());

        // Read the API root's link relations once per session (ADR 0543): the root is the one URL a client may
        // know, and everything else is discovered from it. Fire-and-forget — a workbench that cannot reach the
        // root has larger problems, and the only consequence here is that the affordance stays hidden.
        _ = LoadRootLinksAsync(api);
    }

    private async Task LoadRootLinksAsync(SimplArchiveApiClient api)
    {
        try
        {
            var links = await api.GetRootLinksAsync();
            _myExternalLinksHref = links.TryGetValue("externalLinks", out var href) ? href : null;
            HasMyExternalLinks = _myExternalLinksHref is not null;
        }
        catch (HttpRequestException)
        {
            _myExternalLinksHref = null;
            HasMyExternalLinks = false;
        }
    }

    /// <summary>
    /// What the single WebDAV ribbon button would do next (#461). Its own type rather than four more members
    /// here — this class is already the largest entry on the 1000-line debt list (#466), and the button's state
    /// is genuinely a separate concern.
    /// </summary>
    public WebDavRibbonState WebDav { get; } = new();

    /// <summary>Re-reads the WebDAV button's state; safe to call whenever the session or the mount may have changed.</summary>
    public Task RefreshWebDavStateAsync() =>
        WebDav.RefreshAsync(Api is { } api ? async () => (await api.Profile.GetWebDavStatusAsync()).Enabled : null);

    // ---- Resizable / collapsible panes (persisted, like the web client — ADR 0224/"Desktop collapsible
    // panes") ------------------------------------------------------------------------------------------

    // Two-way bound to the Grid definitions, so a GridSplitter drag updates these. Collapsing sets the size
    // to 0 (content hidden) and remembers the pre-collapse size to restore.
    [ObservableProperty] private GridLength _treeWidth;
    [ObservableProperty] private GridLength _listWidth;
    [ObservableProperty] private GridLength _indexHeight;
    [ObservableProperty] private GridLength _chatWidth;

    [ObservableProperty] private bool _treeCollapsed;
    [ObservableProperty] private bool _listCollapsed;
    [ObservableProperty] private bool _indexCollapsed;
    [ObservableProperty] private bool _chatCollapsed;

    private GridLength _treeSaved, _listSaved, _chatSaved;

    // Default pane proportions (star units) — the reset target and the load-time fallback.
    private const double DefaultTree = 1.4, DefaultList = 2, DefaultChat = 2;

    // Repositories contents-list column widths in pixels (ADR "Desktop list-pane resizable columns"): the
    // header and every row bind their cell widths to these, a horizontal scrollbar appears once the total
    // exceeds the pane, and the header's drag handles call ResizeColumn. Persisted in the layout file.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))] private double _colNameWidth = DefaultColName;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))] private double _colTypeWidth = DefaultColType;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))] private double _colDateWidth = DefaultColDate;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))] private double _colSizeWidth = DefaultColSize;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))] private double _colTagsWidth = DefaultColTags;

    private const double DefaultColName = 260, DefaultColType = 130, DefaultColDate = 96, DefaultColSize = 72, DefaultColTags = 160;
    private const double MinColumnWidth = 48;

    // The total pixel width of all five columns — the fixed width of the scrollable header+rows region, so a
    // horizontal scrollbar kicks in once the columns don't fit the pane.
    public double ContentsTotalWidth => ColNameWidth + ColTypeWidth + ColDateWidth + ColSizeWidth + ColTagsWidth;

    // Resize column `index` (0 Name … 4 Tags) by a pixel delta (from the header's drag handle), clamped to a
    // sensible minimum. Persisting is deferred to the drag's completion / window close.
    public void ResizeColumn(int index, double delta)
    {
        switch (index)
        {
            case 0: ColNameWidth = Math.Max(MinColumnWidth, ColNameWidth + delta); break;
            case 1: ColTypeWidth = Math.Max(MinColumnWidth, ColTypeWidth + delta); break;
            case 2: ColDateWidth = Math.Max(MinColumnWidth, ColDateWidth + delta); break;
            case 3: ColSizeWidth = Math.Max(MinColumnWidth, ColSizeWidth + delta); break;
            case 4: ColTagsWidth = Math.Max(MinColumnWidth, ColTagsWidth + delta); break;
        }
    }

    // Caret glyph for each gutter's collapse toggle (points the way it collapses; flips when collapsed).
    public string TreeCaret => TreeCollapsed ? "mdi-chevron-right" : "mdi-chevron-left";
    public string ListCaret => ListCollapsed ? "mdi-chevron-right" : "mdi-chevron-left";
    public string IndexCaret => IndexCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";
    public string ChatCaret => ChatCollapsed ? "mdi-chevron-left" : "mdi-chevron-right";

    partial void OnTreeCollapsedChanged(bool value) => OnPropertyChanged(nameof(TreeCaret));
    partial void OnListCollapsedChanged(bool value) => OnPropertyChanged(nameof(ListCaret));
    partial void OnIndexCollapsedChanged(bool value) => OnPropertyChanged(nameof(IndexCaret));
    partial void OnChatCollapsedChanged(bool value) => OnPropertyChanged(nameof(ChatCaret));

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
        else { IndexHeight = new GridLength(0); IndexCollapsed = true; }
        SaveLayout();
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

        TreeCollapsed = ListCollapsed = IndexCollapsed = ChatCollapsed = false;

        TreeWidth = _treeSaved;
        ListWidth = _listSaved;
        IndexHeight = GridLength.Auto; // fits its content — there is no default proportion to restore
        ChatWidth = _chatSaved;

        _intrayServerSaved = new GridLength(DefaultIntrayServer, GridUnitType.Star);
        _intrayLocalSaved = new GridLength(DefaultIntrayLocal, GridUnitType.Star);
        _intrayMaskSaved = new GridLength(DefaultIntrayMask, GridUnitType.Star);
        _intrayPreviewSaved = new GridLength(DefaultIntrayPreview, GridUnitType.Star);
        IntrayServerCollapsed = IntrayLocalCollapsed = IntrayMaskCollapsed = IntrayPreviewCollapsed = false;
        IntrayServerHeight = _intrayServerSaved;
        IntrayLocalHeight = _intrayLocalSaved;
        IntrayMaskHeight = _intrayMaskSaved;
        IntrayPreviewHeight = _intrayPreviewSaved;

        ColNameWidth = DefaultColName;
        ColTypeWidth = DefaultColType;
        ColDateWidth = DefaultColDate;
        ColSizeWidth = DefaultColSize;
        ColTagsWidth = DefaultColTags;

        SaveLayout();
        Status = Strings.Get("StLayoutReset");
    }

    private void LoadLayout()
    {
        var settings = LayoutSettingsStore.Load();
        _treeSaved = ParseOrStar(settings.TreeWidth, DefaultTree);
        _listSaved = ParseOrStar(settings.ListWidth, DefaultList);
        _chatSaved = ParseOrStar(settings.ChatWidth, DefaultChat);

        TreeCollapsed = settings.TreeCollapsed;
        ListCollapsed = settings.ListCollapsed;
        IndexCollapsed = settings.IndexCollapsed;
        ChatCollapsed = settings.ChatCollapsed;

        TreeWidth = TreeCollapsed ? new GridLength(0) : _treeSaved;
        ListWidth = ListCollapsed ? new GridLength(0) : _listSaved;
        // Auto, not a persisted value: this pane fits its content (ADR 0550), and a stored height would be the
        // height of whatever happened to be selected when it was last dragged. Nothing reads a saved height for
        // this pane any more — the collapse toggle expands to Auto too.
        IndexHeight = IndexCollapsed ? new GridLength(0) : GridLength.Auto;
        ChatWidth = ChatCollapsed ? new GridLength(0) : _chatSaved;

        _intrayServerSaved = ParseOrStar(settings.IntrayServerHeight, DefaultIntrayServer);
        _intrayLocalSaved = ParseOrStar(settings.IntrayLocalHeight, DefaultIntrayLocal);
        _intrayMaskSaved = ParseOrStar(settings.IntrayMaskHeight, DefaultIntrayMask);
        _intrayPreviewSaved = ParseOrStar(settings.IntrayPreviewHeight, DefaultIntrayPreview);

        IntrayServerCollapsed = settings.IntrayServerCollapsed;
        IntrayLocalCollapsed = settings.IntrayLocalCollapsed;
        IntrayMaskCollapsed = settings.IntrayMaskCollapsed;
        IntrayPreviewCollapsed = settings.IntrayPreviewCollapsed;

        IntrayServerHeight = IntrayServerCollapsed ? new GridLength(0) : _intrayServerSaved;
        IntrayLocalHeight = IntrayLocalCollapsed ? new GridLength(0) : _intrayLocalSaved;
        IntrayMaskHeight = IntrayMaskCollapsed ? new GridLength(0) : _intrayMaskSaved;
        IntrayPreviewHeight = IntrayPreviewCollapsed ? new GridLength(0) : _intrayPreviewSaved;

        ColNameWidth = ParseDouble(settings.ColName, DefaultColName);
        ColTypeWidth = ParseDouble(settings.ColType, DefaultColType);
        ColDateWidth = ParseDouble(settings.ColDate, DefaultColDate);
        ColSizeWidth = ParseDouble(settings.ColSize, DefaultColSize);
        ColTagsWidth = ParseDouble(settings.ColTags, DefaultColTags);
    }

    // Persists the current sizes + collapsed state. Called on each toggle and when the window closes (to
    // capture GridSplitter drag-resizes).
    public void SaveLayout()
    {
        LayoutSettingsStore.Save(new LayoutSettings
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
            IntrayServerHeight = (IntrayServerCollapsed ? _intrayServerSaved : IntrayServerHeight).ToString(),
            IntrayLocalHeight = (IntrayLocalCollapsed ? _intrayLocalSaved : IntrayLocalHeight).ToString(),
            IntrayMaskHeight = (IntrayMaskCollapsed ? _intrayMaskSaved : IntrayMaskHeight).ToString(),
            IntrayPreviewHeight = (IntrayPreviewCollapsed ? _intrayPreviewSaved : IntrayPreviewHeight).ToString(),
            IntrayServerCollapsed = IntrayServerCollapsed,
            IntrayLocalCollapsed = IntrayLocalCollapsed,
            IntrayMaskCollapsed = IntrayMaskCollapsed,
            IntrayPreviewCollapsed = IntrayPreviewCollapsed,
            ColName = ColNameWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColType = ColTypeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColDate = ColDateWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColSize = ColSizeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColTags = ColTagsWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    private static GridLength ParseOrStar(string value, double star)
    {
        try { return GridLength.Parse(value); }
        catch { return new GridLength(star, GridUnitType.Star); }
    }

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    // ---- Intray tab: four collapsible/resizable panes (ADR "Collapsible inbox panes") ------------------
    // Same mechanism as the Repositories panes above — each pane's body row height is two-way bound, collapse
    // sets it to 0, and a header caret toggles it. Persisted in the same LayoutSettings.

    [ObservableProperty] private GridLength _intrayServerHeight;
    [ObservableProperty] private GridLength _intrayLocalHeight;
    [ObservableProperty] private GridLength _intrayMaskHeight;
    [ObservableProperty] private GridLength _intrayPreviewHeight;

    [ObservableProperty] private bool _intrayServerCollapsed;
    [ObservableProperty] private bool _intrayLocalCollapsed;
    [ObservableProperty] private bool _intrayMaskCollapsed;
    [ObservableProperty] private bool _intrayPreviewCollapsed;

    private GridLength _intrayServerSaved, _intrayLocalSaved, _intrayMaskSaved, _intrayPreviewSaved;
    private const double DefaultIntrayServer = 1, DefaultIntrayLocal = 1, DefaultIntrayMask = 1.1, DefaultIntrayPreview = 1.6;

    public string IntrayServerCaret => IntrayServerCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";
    public string IntrayLocalCaret => IntrayLocalCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";
    public string IntrayMaskCaret => IntrayMaskCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";
    public string IntrayPreviewCaret => IntrayPreviewCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";

    partial void OnIntrayServerCollapsedChanged(bool value) => OnPropertyChanged(nameof(IntrayServerCaret));
    partial void OnIntrayLocalCollapsedChanged(bool value) => OnPropertyChanged(nameof(IntrayLocalCaret));
    partial void OnIntrayMaskCollapsedChanged(bool value) => OnPropertyChanged(nameof(IntrayMaskCaret));
    partial void OnIntrayPreviewCollapsedChanged(bool value) => OnPropertyChanged(nameof(IntrayPreviewCaret));

    [RelayCommand]
    private void ToggleIntrayServer()
    {
        if (IntrayServerCollapsed) { IntrayServerHeight = _intrayServerSaved; IntrayServerCollapsed = false; }
        else { _intrayServerSaved = IntrayServerHeight; IntrayServerHeight = new GridLength(0); IntrayServerCollapsed = true; }
        SaveLayout();
    }

    [RelayCommand]
    private void ToggleIntrayLocal()
    {
        if (IntrayLocalCollapsed) { IntrayLocalHeight = _intrayLocalSaved; IntrayLocalCollapsed = false; }
        else { _intrayLocalSaved = IntrayLocalHeight; IntrayLocalHeight = new GridLength(0); IntrayLocalCollapsed = true; }
        SaveLayout();
    }

    [RelayCommand]
    private void ToggleIntrayMask()
    {
        if (IntrayMaskCollapsed) { IntrayMaskHeight = _intrayMaskSaved; IntrayMaskCollapsed = false; }
        else { _intrayMaskSaved = IntrayMaskHeight; IntrayMaskHeight = new GridLength(0); IntrayMaskCollapsed = true; }
        SaveLayout();
    }

    [RelayCommand]
    private void ToggleIntrayPreview()
    {
        if (IntrayPreviewCollapsed) { IntrayPreviewHeight = _intrayPreviewSaved; IntrayPreviewCollapsed = false; }
        else { _intrayPreviewSaved = IntrayPreviewHeight; IntrayPreviewHeight = new GridLength(0); IntrayPreviewCollapsed = true; }
        SaveLayout();
    }

    // ---- Panes ----------------------------------------------------------------------------------------

    public ObservableCollection<TreeNodeViewModel> Tree { get; } = [];
    public ObservableCollection<NodeViewModel> Items { get; } = [];
    public ObservableCollection<IndexFieldViewModel> IndexFields { get; } = [];
    public ObservableCollection<ChatMessageViewModel> Comments { get; } = [];

    [ObservableProperty] private TreeNodeViewModel? _selectedTreeNode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private NodeViewModel? _selectedItem;

    public ObservableCollection<BreadcrumbViewModel> Breadcrumbs { get; } = [];
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _maskLine = "";

    // System fields — always shown, separate from the mask (ADR "System fields + OCR-language mask field").
    // Name / DocumentDate / OCR languages are read-write; Created / CreatedBy / File extension are read-only.
    // Every read-write field is only editable while the whole pane is in edit mode (ADR "Single pane-level
    // edit toggle on the detail pane"). OCR languages shows only for a TIFF-sourced document.
    [ObservableProperty] private string _sysName = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SysDocumentDateText))] private DateTimeOffset? _sysDocumentDate;
    [ObservableProperty] private string _sysCreated = "";
    [ObservableProperty] private string _sysCreatedBy = "";
    [ObservableProperty] private string _sysFileExtension = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanEditOcr))] private bool _sysHasTiff;
    [ObservableProperty] private string _sysOcrLanguages = "";
    // The document's current (latest confirmed) version number — the last line of the detail pane (ADR "Mask-pane
    // current-version line"). Empty for a folder / a document with no confirmed version.
    [ObservableProperty] private string _sysCurrentVersion = "";

    // Sensitivity label (ADR "Configurable sensitivity labels + upload defaults"): the current per-tenant label
    // (read-only display: name + colour) + the staged edit value bound to the ComboBox (a picker item).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasSensitivity))][NotifyPropertyChangedFor(nameof(DetailSensitivityText))][NotifyPropertyChangedFor(nameof(DetailSensitivityBrush))] private Guid? _detailSensitivityId;
    private string _detailSensitivityName = "";
    private string? _detailSensitivityColor;
    private bool _detailSensitivityWatermark;
    public string DetailSensitivityText => _detailSensitivityName;
    // A label with no colour of its own falls back to the accent. Taken from the design tokens rather than
    // written here (ADR 0578) — the light value specifically, because this is a filled chip whose text is
    // white in both themes, so the darker of the two accents is the one that keeps it readable.
    public string DetailSensitivityBrush => string.IsNullOrEmpty(_detailSensitivityColor)
        ? ThemeTokensReader.Shipped.Light.Accent.Primary
        : _detailSensitivityColor;
    public bool HasSensitivity => DetailSensitivityId != null;

    // The picker: "(None)" + the tenant's active labels; SelectedSensitivityItem is the staged edit value.
    public ObservableCollection<SensitivityPickerItem> SensitivityPickerItems { get; } = [];
    [ObservableProperty] private SensitivityPickerItem? _selectedSensitivityItem;
    // The full label catalog (for the management dialog + the picker), loaded on login.
    public ObservableCollection<AdminClient.SensitivityLabelInfo> SensitivityCatalog { get; } = [];

    public sealed record SensitivityPickerItem(Guid? Id, string Name);

    private void RebuildSensitivityPicker()
    {
        SensitivityPickerItems.Clear();
        SensitivityPickerItems.Add(new SensitivityPickerItem(null, "(None)"));
        foreach (var l in SensitivityCatalog.Where(l => !l.Retired))
        {
            SensitivityPickerItems.Add(new SensitivityPickerItem(l.Id, l.Name));
        }
    }

    public async Task LoadSensitivityCatalogAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var catalog = await _api.Admin.GetSensitivityLabelsAsync();
            SensitivityCatalog.Clear();
            foreach (var l in catalog.Items)
            {
                SensitivityCatalog.Add(l);
            }

            CanManageSensitivity = catalog.CanManage;
            RebuildSensitivityPicker();
            RebuildClearanceOptions();
        }
        catch (Exception) { }
    }

    // Clearance options for the Users & groups clearance combo (ADR "Sensitivity clearance enforcement"): rank 0
    // (none) plus one per active label rank.
    public sealed record ClearanceOption(int Rank, string Label);

    public ObservableCollection<ClearanceOption> ClearanceOptions { get; } = [];

    private void RebuildClearanceOptions()
    {
        ClearanceOptions.Clear();
        ClearanceOptions.Add(new ClearanceOption(0, "0 — None (unlabelled only)"));
        foreach (var l in SensitivityCatalog.Where(l => !l.Retired).OrderBy(l => l.Rank))
        {
            ClearanceOptions.Add(new ClearanceOption(l.Rank, $"{l.Rank} — {l.Name}"));
        }
    }

    // The selected principal's clearance rank (edited alongside the rights matrix, saved together).
    [ObservableProperty] private int _selectedPrincipalClearance;

    [ObservableProperty] private bool _canManageSensitivity;

    // Builds the management dialog's VM (ADR "Configurable sensitivity labels + upload defaults"); null when not
    // signed in. The caller reloads the catalog when the dialog closes.
    public SensitivityLabelsViewModel? CreateSensitivityLabelsViewModel() =>
        _api is { } api ? new SensitivityLabelsViewModel(api) : null;

    // Free-form tags (ADR "Document tags"): the selected document's tags (read-only chips), the edit working
    // copy (chips with a remove + an add box over the tenant catalog), and the pending new-tag value.
    public ObservableCollection<string> DetailTags { get; } = [];
    public ObservableCollection<string> EditTags { get; } = [];
    public ObservableCollection<string> TagCatalog { get; } = [];
    [ObservableProperty] private bool _hasDetailTags;
    [ObservableProperty] private string _newTag = "";
    private List<string> _origTags = [];

    // Follow / unfollow the selected document (ADR "Document subscriptions") — set from the detail load; the
    // toggle glyph + tooltip switch on it.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SubscriptionIcon))][NotifyPropertyChangedFor(nameof(SubscriptionTip))] private bool _detailSubscribed;
    public string SubscriptionIcon => DetailSubscribed ? "mdi-bell" : "mdi-bell-outline";
    public string SubscriptionTip => DetailSubscribed ? "Unfollow — stop notifications about this document" : "Follow — get notified when this document changes";

    [RelayCommand]
    private async Task ToggleSubscriptionAsync()
    {
        if (_api is not { } api || _detailLinks is null)
        {
            return;
        }

        try
        {
            var target = !DetailSubscribed;
            await api.Documents.SetSubscriptionAsync(DetailHref("subscription"), target);
            DetailSubscribed = target;
            Status = target ? "Following this document." : "Unfollowed.";
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrSubscription");
        }
    }

    // Read-only display of the document date (edit mode uses the DatePicker bound to SysDocumentDate).
    public string SysDocumentDateText => SysDocumentDate?.ToString("yyyy-MM-dd") ?? "";

    private Guid _sysCurrentVersionId;

    // The current version's advertised `document-date` address (ADR 0543) — null until the system fields load.
    private string? _sysDocumentDateHref;

    // The notification collection's advertised `read-all` address — null until the bell has loaded once.
    private string? _notificationsReadAllHref;
    private IReadOnlyList<string> _sysOcrCodes = [];  // persisted (original) OCR codes
    private IReadOnlyList<string> _stagedOcrCodes = []; // picker-staged codes, persisted on Save
    private IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> _ocrCatalog = [];

    // The Repositories + Intray preview surface (state + render + find + hit-overlay + full-screen). Extracted to
    // its own PreviewViewModel so the Recycle bin tab can own a SEPARATE instance (RecycleBin.Preview) and the
    // two previews are never entangled — see ADR "Desktop recycle bin parity". Bound by the PreviewPane control.
    public PreviewViewModel Preview { get; } = new();

    // The Intray tab owns a SEPARATE preview instance so its preview never entangles the Repositories one — a
    // preview shown on one tab must not leak onto the other (mirrors RecycleBin.Preview). Bound by the Intray
    // PreviewPane.
    public PreviewViewModel IntrayPreview { get; } = new();

    // The Search tab's own preview instance, for the same reason as the Intray's (#462): a preview shown while
    // browsing search results must not leak into the Repositories tab, and vice versa.
    public PreviewViewModel SearchPreview { get; } = new();

    // Leaves full-screen for ALL preview surfaces (the Esc key binding + a tab switch) — only the active tab's
    // preview can actually be full-screen, so clearing all is safe.
    [RelayCommand]
    private void ExitPreviewFullscreen()
    {
        Preview.ExitFullscreen();
        IntrayPreview.ExitFullscreen();
        RecycleBin.Preview.ExitFullscreen();
    }

    [ObservableProperty] private string _newComment = "";

    // ---- @-mentions (issue #383) ----------------------------------------------------------------------------

    // The picker's endpoint, as the thread advertised it (ADR 0543) — the server decides who may be addressed
    // here, because offering somebody who cannot see the document would leak the document to them.
    private string? _mentionableUsersHref;

    public ObservableCollection<DocumentsClient.MentionableUser> MentionCandidates { get; } = [];

    // An explicit bool rather than binding IsVisible straight to Count: Avalonia does not convert int to bool for
    // a visibility binding, so the picker would simply never hide.
    [ObservableProperty] private bool _hasMentionCandidates;

    // Typing drives the picker. The query is whatever follows the LAST '@' and deliberately does not stop at a
    // space — display names contain them, so "@Demo Ad" has to keep matching — which is why it is capped instead.
    private const int MentionQueryLimit = 30;

    partial void OnNewCommentChanged(string value) => Safe.Fire(() => RefreshMentionCandidatesAsync(value));

    private async Task RefreshMentionCandidatesAsync(string text)
    {
        if (_api is null || _mentionableUsersHref is not { } href || MentionQuery(text) is not { } query)
        {
            MentionCandidates.Clear();
            HasMentionCandidates = false;
            return;
        }

        var users = await _api.Documents.GetMentionableUsersAsync(href, query);

        MentionCandidates.Clear();
        foreach (var user in users)
        {
            MentionCandidates.Add(user);
        }

        HasMentionCandidates = MentionCandidates.Count > 0;
    }

    private static string? MentionQuery(string text)
    {
        var at = text.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        // The '@' has to start the token — otherwise an email address in the text would open the picker.
        if (at > 0 && !char.IsWhiteSpace(text[at - 1]))
        {
            return null;
        }

        var query = text[(at + 1)..];
        return query.Length > MentionQueryLimit || query.Contains('\n') ? null : query;
    }

    // Replaces the half-typed "@Dem" with the token, so what is STORED is the id and what is SHOWN is the name.
    [RelayCommand]
    private void PickMention(DocumentsClient.MentionableUser? user)
    {
        if (user is null)
        {
            return;
        }

        var at = NewComment.LastIndexOf('@');
        if (at < 0)
        {
            return;
        }

        NewComment = $"{NewComment[..at]}@[{user.Id}] ";
        MentionCandidates.Clear();
        HasMentionCandidates = false;
    }

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

    public Task<byte[]>? ExportRepositoryBytesAsync(DocumentsClient.RepositoryExportOptions options) =>
        _currentFolderLinks is { } links && _api is { } api ? ExportRepositoryCoreAsync(api, links, options) : null;

    // The export rel lives on the document RESOURCE, not the listing row — one fetch of the folder's own
    // resource, then the follow (ADR 0559).
    private static async Task<byte[]> ExportRepositoryCoreAsync(SimplArchiveApiClient api, IReadOnlyDictionary<string, string> folderLinks, DocumentsClient.RepositoryExportOptions options) =>
        await api.Documents.ExportRepositoryAsync(await api.Documents.RelViaSelfAsync(folderLinks["self"], "export"), options);

    // Imports an archive (ADR "Repository import") under the current folder, or as a new repository when at the
    // repository-list root, then rebuilds the tree so the imported content shows. Returns null if not signed in.
    public async Task<DocumentsClient.ImportResultInfo?> ImportAndReloadAsync(byte[] zip, bool updateExisting, bool includePermissions, bool merge, string leafConflict = "rename")
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
        var result = await api.Documents.ImportRepositoryAsync(importHref, zip, updateExisting, includePermissions, merge, leafConflict);
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

    // "Manage access …" appears for any real folder or document (not a reference/archive row). ACLs apply to
    // folders and documents alike; the dialog self-gates on the caller's CanManagePermissions (ADR 0486).
    public bool CanManageAccess => SelectedItem is { IsReference: false, IsArchiveEntry: false, IsArchiveBack: false };

    // The tree context menu's "References …" entry mirrors SelectedHasReferences, but for the RIGHT-CLICKED tree
    // node rather than the contents-list selection (ADR "Tree-pane context menu"). MainWindow sets it before the
    // menu opens.
    [ObservableProperty] private bool _treeContextHasReferences;

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
        OnPropertyChanged(nameof(CanCheckOut));
        OnPropertyChanged(nameof(CanOverrideSelected));
        OnPropertyChanged(nameof(DetailIsFolder)); // the pane's subject changed, and with it the folder-only row
        if (value is { IsFolder: false, IsArchiveEntry: false })
        {
            await LoadDetailAsync(value);
        }
    }

    // ---- Login ----------------------------------------------------------------------------------------

    // Set by Log out so the next sign-in forces a fresh browser login (prompt=login) rather than silently
    // re-using the browser's session cookie — letting a different tenant/user log in (ADR "Desktop logout").
    private bool _forceLoginNext;

    [RelayCommand]
    private async Task LoginAsync()
    {
        Status = Strings.Get("StOpeningBrowser");
        try
        {
            var result = await _authenticator.AuthenticateAsync(forceLogin: _forceLoginNext);
            if (result is null)
            {
                Status = Strings.Get("StSignInFailed");
                return;
            }

            _forceLoginNext = false;
            UseApi(new SimplArchiveApiClient(result.AccessToken));
            UserEmail = result.Email ?? "(unknown)";
            IsLoggedIn = true;
            await SetupUserContextAsync();
            await LoadRootAsync();
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrSignIn"), e.Message);
        }
    }

    // Log out: drop the in-memory session + all loaded state, and force the next sign-in to re-authenticate in
    // the browser (so a different tenant/user can log in). See ADR "Desktop logout / switch user".
    [RelayCommand]
    private void Logout()
    {
        _api = null;
        Preview.Api = null;
        IntrayPreview.Api = null;
        IsLoggedIn = false;
        _forceLoginNext = true;
        _ = StopRealtimeNotificationsAsync(); // drop the live hub connection with the session

        // Clear loaded state so nothing from the previous session lingers behind the login prompt.
        Tree.Clear();
        Items.Clear();
        Breadcrumbs.Clear();
        SelectedItem = null;
        SelectedTreeNode = null;
        ClearDetail();
        _currentFolderId = null;
        _currentRepositoryId = null;
        _archiveDocumentId = null;
        _localFolders = null;
        _currentUserId = null;
        UserEmail = "";
        UserDisplayName = "";

        // Reset the right-gated tabs so the next user's rights apply cleanly.
        IsTenantAdmin = false;
        CanManageUsers = false;
        CanManageServiceAccounts = false;
        CanImpersonate = false;
        IsImpersonating = false;
        ImpersonatedName = null;
        _adminApi = null;
        CanViewAuditLog = false;
        CanLegalHold = false;
        CanManageClassification = false;
        CanOverrideCheckout = false;
        HasExportRight = false;
        HasImportRight = false;
        TenantSettingsLoaded = false;
        TenantEditingGroup = null;
        Notifications.Clear();
        UnreadNotificationCount = 0;
        SavedSearches.Clear();
        LastSearchQueryString = "";
        SelectedTab = 0;
        Status = Strings.Get("StSignedOut");

        // Return to the startup logon window (ADR "Desktop logon window") — the app re-shows it and closes this
        // window. Falls back to the in-window state if nothing is subscribed.
        LogoutRequested?.Invoke();
    }

    // Raised by Logout so the app can return to the logon window (login redesign slice B).
    public event Action? LogoutRequested;

    // Bootstraps an already-authenticated session (the logon window did the OAuth, ADR "Desktop logon window") —
    // mirrors LoginAsync's post-authentication steps without opening the browser here.
    public async Task InitializeSessionAsync(SimplArchiveApiClient api, string email)
    {
        UseApi(api);
        UserEmail = email;
        IsLoggedIn = true;
        await SetupUserContextAsync();
        await LoadRootAsync();
    }

    private async Task LoadRootAsync()
    {
        if (_api is null)
        {
            return;
        }

        await ReloadTreeAsync();

        Items.Clear();
        ClearDetail();
        _currentFolderId = null;
        _currentRepositoryId = null;
        CanCreateFolder = false;
        CanExport = false;
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        Status = string.Format(Strings.Get("StRepositories"), Tree.Count);
    }

    // Rebuilds the folders-only tree from the top (repository roots), collapsed. The tree lazy-loads and
    // caches each node's children on first expand, so a structural change (new/deleted/moved folder) isn't
    // reflected until the tree is rebuilt — hence Refresh and folder-creating operations call this. Same
    // whole-tree-reload simplification as the web client (the tree collapses).
    private async Task ReloadTreeAsync()
    {
        if (_api is null)
        {
            return;
        }

        var repositories = await _api.Documents.GetRepositoriesAsync();
        Tree.Clear();

        // The user's personal repository, pinned at the top (ADR "Per-user personal repository"). It's excluded
        // from GetRepositoriesAsync, so it never appears twice.
        var personal = await _api.Profile.GetPersonalRepositoryAsync();
        if (personal is not null)
        {
            // Always expandable — it holds at least the Intray + Check-out launcher nodes (ADR "GUI-tree Personal
            // space grouping"), even before any real subfolder exists.
            Tree.Add(new TreeNodeViewModel(personal.Id, personal.Name, hasSubfolders: true, LoadPersonalChildrenAsync, links: personal.Links, isPersonal: true));
        }

        // Shared repositories sorted alphabetically (issue #339); Personal stays pinned above them.
        foreach (var repository in repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            Tree.Add(new TreeNodeViewModel(repository.Id, repository.Name, repository.HasSubfolders, LoadTreeChildrenAsync, links: repository.Links, hasReferences: repository.HasReferences, hasChildren: repository.HasChildren));
        }

        // Tenant admins get a synthetic "Administration → Users" branch (ADR "Tenant-admin Administration → Users
        // view") to browse every user's personal space; its children load from the admin endpoint.
        if (IsTenantAdmin)
        {
            Tree.Add(new TreeNodeViewModel(Guid.Empty, "Administration", true, LoadAdminRootAsync, syntheticIcon: "mdi-shield-account"));
        }
    }

    // After a subfolder is created under parentId, refresh just that node's children in place + keep it expanded,
    // so the tree keeps showing the parent folder (whose contents are in the list pane) instead of collapsing to
    // the roots (ADR "Keep the desktop tree expanded on a structural change"). Falls back to a full rebuild only
    // if the parent isn't currently materialised in the tree (e.g. reached by drilling through the list pane).
    private async Task ShowNewChildInTreeAsync(Guid parentId)
    {
        if (FindTreeNode(Tree, parentId) is { } node)
        {
            await node.ReloadChildrenAsync();
        }
        else
        {
            await ReloadTreeAsync();
        }
    }

    private static TreeNodeViewModel? FindTreeNode(IEnumerable<TreeNodeViewModel> nodes, Guid id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id && !n.IsSynthetic && !n.IsLauncher)
            {
                return n;
            }
            if (FindTreeNode(n.Children, id) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private Task<IEnumerable<TreeNodeViewModel>> LoadAdminRootAsync(TreeNodeViewModel _) =>
        Task.FromResult<IEnumerable<TreeNodeViewModel>>(
            [new TreeNodeViewModel(Guid.Empty, "Users", true, LoadAdminUsersAsync, syntheticIcon: "mdi-account-group")]);

    private async Task<IEnumerable<TreeNodeViewModel>> LoadAdminUsersAsync(TreeNodeViewModel _)
    {
        var repos = await _api!.Admin.GetAdminPersonalRepositoriesAsync();
        // Each user's personal repo is a normal browsable node (Id = the repo; the admin's ACL bypass grants it).
        return repos.Select(r => new TreeNodeViewModel(
            r.RepositoryId,
            r.UserIsActive ? r.DisplayName : $"{r.DisplayName} (inactive)",
            r.HasSubfolders,
            LoadTreeChildrenAsync,
            isPersonal: true,
            hasChildren: r.HasChildren));
    }

    // The Personal repository nests the Intray + Check-out launcher nodes above its real subfolders, mirroring
    // /webdav/Personal (ADR "GUI-tree Personal space grouping"). Selecting a launcher switches to the matching
    // bottom tab (OnSelectedTreeNodeChanged), where the full staging / check-out UX lives.
    private async Task<IEnumerable<TreeNodeViewModel>> LoadPersonalChildrenAsync(TreeNodeViewModel node)
    {
        var launchers = new[]
        {
            new TreeNodeViewModel(Guid.Empty, "Intray", false, null, personalKind: "intray"),
            new TreeNodeViewModel(Guid.Empty, "Check-out", false, null, personalKind: "checkout"),
        };
        return launchers.Concat(await LoadTreeChildrenAsync(node));
    }

    private async Task<IEnumerable<TreeNodeViewModel>> LoadTreeChildrenAsync(TreeNodeViewModel node)
    {
        // The tree shows folders only — real child folders plus references whose target is a folder (a
        // shortcut node whose Id is the target folder, so it expands the target's subtree). See ADR
        // "Referenced folder in the tree".
        // Folders are always sorted alphabetically in the tree (issue #339) — the children endpoint orders by
        // creation for its cursor, so re-sort by name here (all pages are loaded).
        var children = await _api!.Documents.GetChildrenAsync(node.Href("children"));
        var folderNodes = children
            .Where(c => !c.HasVersions)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new TreeNodeViewModel(c.Id, c.Name, c.HasSubfolders, LoadTreeChildrenAsync, links: c.Links, hasReferences: c.HasReferences, hasChildren: c.HasChildren));

        var references = await _api.Documents.GetReferencesAsync(node.Href("references"));
        var referenceNodes = references
            .Where(r => !r.HasVersions)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new TreeNodeViewModel(r.TargetId, r.Name, r.HasSubfolders, LoadTreeChildrenAsync, isReference: true, hasReferences: r.HasReferences, hasChildren: r.HasChildren));

        return folderNodes.Concat(referenceNodes);
    }

    // ---- Contents / breadcrumb ------------------------------------------------------------------------

    // folderLinks: the addresses the caller already holds — a tree node's or a list row's. When null (a restored
    // selection, an import that only knows an id) this reads the folder resource ONCE and follows its rels from
    // there: `children` for the contents and `references` for the shortcuts, with the contents order riding in
    // the children envelope. One read, three follows — never a composed sub-resource path, and never a fetch per
    // rel, which is the failure mode that talks a codebase back into string paths (ADR 0543, issue #416).
    private async Task LoadFolderContentsAsync(Guid folderId, IReadOnlyDictionary<string, string>? folderLinks = null)
    {
        if (_api is null)
        {
            return;
        }

        // Navigating to a *different* folder resets the panes right of the list (index-data / preview / comments)
        // so they don't keep showing the previously-selected document — parity with the web client, whose
        // SelectFolderAsync clears the detail on every folder selection (ADR 0516). A same-folder reload (after an
        // in-place operation such as a legal-hold toggle or an upload) deliberately keeps the current detail; the
        // operation handlers that need to clear it call ClearDetail() themselves.
        if (_currentFolderId != folderId)
        {
            SelectedItem = null;
            ClearDetail();
        }

        var isReload = _currentFolderId == folderId;
        _archiveDocumentId = null; // leave any archive-browsing view
        _currentFolderId = folderId;
        CanCreateFolder = true;
        CanExport = true;
        Status = Strings.Get("StLoading");
        try
        {
            // Use the caller's links only when they carry BOTH addresses this needs. A listing advertises a
            // fixed set, and not every listing advertises the same one — so "the caller passed something" is not
            // the same as "the caller passed enough". A partial set is completed by reading the resource at ITS
            // OWN advertised document address (a repository row calls the document view `document`, ADR 0200);
            // a reload of the folder already open reuses its stored links. There is no id fallback any more —
            // an id alone has no address (ADR 0543, #443).
            var links = folderLinks is not null && folderLinks.ContainsKey("children") && folderLinks.ContainsKey("references")
                ? folderLinks
                : folderLinks is not null && (folderLinks.TryGetValue("document", out var ownAddress) || folderLinks.TryGetValue("self", out ownAddress))
                    ? await _api.Documents.GetDocumentLinksAsync(ownAddress)
                    : isReload && _currentFolderLinks is { } stored
                        ? stored
                        : throw new InvalidOperationException($"No advertised address for folder '{folderId}' (ADR 0543).");
            _currentFolderLinks = links;
            // The folder's persisted default contents order (ADR "Per-folder contents sort order") arrives with
            // the contents; opening a fresh folder resets any ephemeral column-header sort back to that default.
            var (children, sortOrder) = await _api.Documents.GetFolderContentsAsync(links["children"]);
            var references = await _api.Documents.GetReferencesAsync(links["references"]);
            _folderSortOrder = sortOrder;
            _headerSortActive = false;
            OnPropertyChanged(nameof(DetailSortText));
            OnPropertyChanged(nameof(DetailIsFolder));
            Items.Clear();
            foreach (var child in children)
            {
                Items.Add(new NodeViewModel
                {
                    Links = child.Links,
                    Id = child.Id,
                    Name = child.Name,
                    HasChildren = child.HasChildren,
                    HasVersions = child.HasVersions,
                    HasReferences = child.HasReferences,
                    OnLegalHold = child.OnLegalHold,
                    CheckedOut = child.CheckedOut,
                    CheckedOutByMe = child.CheckedOutByMe,
                    CheckedOutByName = child.CheckedOutByName,
                    DocumentType = child.DocumentType,
                    DocumentDate = child.DocumentDate,
                    SizeBytes = child.SizeBytes,
                    Tags = child.Tags ?? [],
                    SensitivityLabelName = child.SensitivityLabelName,
                    SensitivityLabelColor = child.SensitivityLabelColor,
                    VersionCount = child.VersionCount,
                    VersionCreatedAt = child.VersionCreatedAt,
                });
            }

            // References (shortcuts) filed in this folder, rendered with a shortcut icon — see ADR "Desktop
            // drag-and-drop move and reference". Id is the target, so Open/Save-as/detail act on it.
            foreach (var reference in references)
            {
                Items.Add(new NodeViewModel
                {
                    // The server advertises the same target sub-resources a children row gets (#416), so the
                    // shortcut row is no less capable than the real row beside it.
                    Links = reference.Links,
                    Id = reference.TargetId,
                    Name = reference.Name,
                    HasChildren = reference.HasChildren,
                    HasVersions = reference.HasVersions,
                    HasReferences = reference.HasReferences,
                    IsReference = true,
                    ReferenceId = reference.ReferenceId,
                    ReferenceDeleteHref = reference.DeleteHref,
                    RealParentId = reference.RealParentId,
                });
            }

            ApplyContentSort(); // keep the chosen column sort across folder navigation
            Status = string.Format(Strings.Get("StItems"), Items.Count);
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad"), e.Message);
        }
    }

    // ---- Contents-list column sorting (ADR "List-row columns and sorting") — client-side over the loaded
    // rows, since the listing is cursor-paginated by creation order. -----------------------------------
    // Folders are ALWAYS listed on top (ADR "Per-folder contents sort order"); within each group the default is
    // the open folder's persisted _folderSortOrder (0=Name/1=DocumentDate/2=Created). Clicking a column header is
    // an EPHEMERAL override (_headerSortActive) that resets when another folder is opened.
    // The open folder's advertised addresses, stored with its id so a same-folder reload and the
    // folder-scoped actions (create folder, upload, export, import) follow what the navigation row carried
    // (ADR 0555) instead of re-deriving anything from the id.
    private IReadOnlyDictionary<string, string>? _currentFolderLinks;

    private int _folderSortOrder = 1; // DocumentDate
    private bool _headerSortActive;
    private string _contentSortColumn = "name";
    private bool _contentSortAscending = true;

    public string NameHeader => ColumnHeader("name", "Name");
    public string TypeHeader => ColumnHeader("type", "Type");
    public string DateHeader => ColumnHeader("date", "Doc date");
    public string SizeHeader => ColumnHeader("size", "Size");
    public string TagsHeader => ColumnHeader("tags", "Tags");
    private string ColumnHeader(string col, string label) => _headerSortActive && _contentSortColumn == col ? $"{label} {(_contentSortAscending ? "▲" : "▼")}" : label;

    [RelayCommand]
    private void SortContents(string column)
    {
        if (_headerSortActive && _contentSortColumn == column)
        {
            _contentSortAscending = !_contentSortAscending;
        }
        else
        {
            _contentSortColumn = column;
            _contentSortAscending = true;
            _headerSortActive = true; // an ephemeral override, until the next folder is opened
        }

        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(TypeHeader));
        OnPropertyChanged(nameof(DateHeader));
        OnPropertyChanged(nameof(SizeHeader));
        OnPropertyChanged(nameof(TagsHeader));
        ApplyContentSort();
    }

    private void ApplyContentSort()
    {
        if (Items.Count < 2)
        {
            return;
        }

        // Folders on top (always alphabetical, issue #339), then documents ordered by the active criterion (the
        // default is DocumentDate). A column-header click is an explicit ephemeral override of the whole list.
        var folders = Items.Where(n => n.IsFolder);
        var docs = Items.Where(n => !n.IsFolder);
        var sorted = _headerSortActive
            ? HeaderSort(folders).Concat(HeaderSort(docs)).ToList()
            : folders.OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).Concat(FolderSort(docs)).ToList();
        Items.Clear();
        foreach (var n in sorted)
        {
            Items.Add(n);
        }
    }

    private IEnumerable<NodeViewModel> FolderSort(IEnumerable<NodeViewModel> items) => _folderSortOrder switch
    {
        1 => items.OrderBy(n => n.DocumentDate ?? DateOnly.MinValue).ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase),
        2 => items.OrderBy(n => n.VersionCreatedAt ?? DateTimeOffset.MinValue).ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase),
        _ => items.OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase),
    };

    private IEnumerable<NodeViewModel> HeaderSort(IEnumerable<NodeViewModel> items)
    {
        IEnumerable<NodeViewModel> ordered = _contentSortColumn switch
        {
            "type" => items.OrderBy(n => n.DocumentType, StringComparer.OrdinalIgnoreCase),
            "date" => items.OrderBy(n => n.DocumentDate ?? DateOnly.MinValue),
            "size" => items.OrderBy(n => n.SizeBytes ?? -1),
            "tags" => items.OrderBy(n => n.TagsText, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
        return _contentSortAscending ? ordered : ordered.Reverse();
    }

    // ---- Folder detail pane: the open folder's persisted contents sort order (ADR "Per-folder contents sort
    // order") — a "Contents sort order" field editable under an Edit toggle. -----------------------------
    public string FolderSortText => _folderSortOrder switch
    {
        1 => Strings.Get("FolderSortDocDate"),
        2 => Strings.Get("FolderSortCreated"),
        _ => Strings.Get("FolderSortName"),
    };

    // A folder is open and no document is selected → show the folder detail pane instead of the placeholder.
    // The pane describes a SUBJECT — a selected row, else the open folder — so a folder gets the document's pane
    // wherever you reached it from (issue #408). ShowFolderDetail, which used to gate a pane of its own, is gone.
    public bool DetailIsFolder => _detailIsFolder;

    // The glyph and colour the TREE would give this same object (ADR 0547), so a folder looks like a folder — and
    // an empty one like an empty one — in the detail pane too, rather than changing style per pane. Reuses
    // TreeNodeViewModel's rules rather than restating them, so the two cannot drift.
    public string DetailGlyph => _detailGlyphNode?.IconValue ?? "mdi-file-document-outline";

    public string DetailGlyphBrushKey => _detailGlyphNode?.IconBrushKey ?? "WbMuted";

    private TreeNodeViewModel? _detailGlyphNode;

    private bool _detailIsFolder;


    private int _detailSortOrder;

    // Staged like every other edited field, so Cancel discards it and Save commits it with the rest.
    [ObservableProperty] private int _editSortOrder;

    public string DetailSortText => _detailSortOrder switch
    {
        1 => Strings.Get("FolderSortDocDate"),
        2 => Strings.Get("FolderSortCreated"),
        _ => Strings.Get("FolderSortName"),
    };

    // The tree context menu's "Contents sort order" entry: the order is a field in the ONE detail pane now
    // (issue #408), so this opens that edit rather than a mode of its own — one edit, one Save, wherever you
    // started from. The old toggle (its own Edit/Save/Cancel and its own save path) is gone with it.
    [RelayCommand]
    private void BeginFolderSortEdit() => BeginEditCommand.Execute(null);

    // The folder currently shown in the contents pane — the drop target for a drag onto empty space.
    public Guid? CurrentFolderId => _currentFolderId;

    // Rebuilds the breadcrumb from a tree node's ancestry (root → … → node).
    private void SetBreadcrumbFromTreeNode(TreeNodeViewModel node)
    {
        var chain = new List<TreeNodeViewModel>();
        for (var current = node; current is not null; current = current.Parent)
        {
            chain.Add(current);
        }

        chain.Reverse();

        // The top-level ancestor is the repository root — the scope the recycle bin lists against.
        _currentRepositoryId = chain[0].Id;

        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        foreach (var ancestor in chain)
        {
            Breadcrumbs.Add(new BreadcrumbViewModel { Name = ancestor.Name, FolderId = ancestor.Id, Links = ancestor.Links, ShowSeparator = true });
        }
    }

    // Navigate to a clicked crumb: the root crumb reloads repositories; a folder crumb truncates the path
    // back to it and loads its contents.
    [RelayCommand]
    private async Task NavigateToBreadcrumb(BreadcrumbViewModel? crumb)
    {
        if (crumb is null)
        {
            return;
        }

        if (crumb.FolderId is not { } folderId)
        {
            await LoadRootAsync();
            return;
        }

        var index = Breadcrumbs.IndexOf(crumb);
        for (var i = Breadcrumbs.Count - 1; i > index; i--)
        {
            Breadcrumbs.RemoveAt(i);
        }

        await LoadFolderContentsAsync(folderId, crumb.Links);
    }

    // "Open (⌘O)" for the affordances that are a plain button rather than a menu entry — the ribbon's Open and
    // the Intray row's Open — since only a MenuItem can carry an InputGesture. Composed from the localized label
    // plus the platform chord, so it needs no resource of its own.
    // Trailing after a dash, not in brackets: the ribbon's own label is already a parenthesised sentence, and a
    // second bracket inside a tooltip reads as a nested aside rather than as a shortcut.
    private static string WithOpenChord(string labelKey) => $"{Strings.Get(labelKey)} — {Services.Shortcuts.Open}";

    public static string OpenTip => WithOpenChord("MwOpen");
    public static string RibbonOpenTip => WithOpenChord("RibbonOpen");

    // ⌘/Ctrl+O on the current tab's selected row (#482, ADR "One shortcut for opening a document"). Opening is
    // the most frequent action in the product and needed a right-click and a menu pick every time.
    //
    // Deliberately only the two tabs whose Open means **open in the native application**. Search and Tasks have
    // an "Open" too, but theirs REVEALS the document in Repositories — a different action wearing the same word,
    // and one chord that means two things is a chord nobody trusts. Check-out and the Recycle bin have no Open
    // at all, and ADR 0554 says an action that cannot succeed is not advertised.
    //
    // Addressed from the SELECTION, never from a pane's loaded state (ADR 0559): both commands read the selected
    // row, which is set synchronously on click, so a shortcut pressed mid-load still acts on what is selected.
    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        switch (SelectedTab)
        {
            case 0 when OpenCommand.CanExecute(null):
                await OpenCommand.ExecuteAsync(null);
                break;
            case 1 when SelectedServerIntrayItem is not null:
                await OpenServerIntrayItemCommand.ExecuteAsync(null);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenAsync()
    {
        if (SelectedItem is not { } node || _api is null)
        {
            return;
        }

        if (node.IsArchiveBack)
        {
            await ExitArchiveAsync();
            return;
        }

        if (node.IsArchiveEntry)
        {
            // A zip entry has no presigned URL — fetch its bytes through the authenticated Api, write them to
            // the temp folder, and hand off to the OS app (task #7, ADR "Zip file browsing").
            try
            {
                var bytes = await DownloadArchiveEntryAsync(node);
                if (bytes is null)
                {
                    Status = string.Format(Strings.Get("StErrReadArchive"), node.Name);
                    return;
                }

                await NativeFileOpener.OpenBytesAsync(bytes, Path.GetFileName(node.Name.Replace('\\', '/')));
                Status = string.Format(Strings.Get("StOpenedNative"), node.Name);
            }
            catch (Exception e)
            {
                Status = string.Format(Strings.Get("StErrOpen2"), node.Name, e.Message);
            }

            return;
        }

        if (node.IsFolder || node.HasChildren)
        {
            // Drill into a folder, or a document that has child documents (an email with filed attachments,
            // ADR "Email attachments as child documents") — append it to the breadcrumb path and list its
            // contents.
            Breadcrumbs.Add(new BreadcrumbViewModel { Name = node.Name, FolderId = node.Id, ShowSeparator = Breadcrumbs.Count > 0, Links = node.Links });
            await LoadFolderContentsAsync(node.Id, node.Links);
            return;
        }

        try
        {
            // Fetch the preview to resolve the version's file extension (Document.Name is a bare stem now —
            // ADR "Extension off Document.Name"), needed both to spot a .zip and to name the opened temp file.
            var preview = await _api.Documents.GetPreviewAsync(node.Href("versions"));

            if (node.HasVersions && string.Equals(preview.FileExtension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Browse the .zip's entries virtually — nothing unpacked (ADR "Zip file browsing").
                await EnterArchiveAsync(node);
                return;
            }

            if (preview.DownloadUrl is null)
            {
                Status = string.Format(Strings.Get("StNoDownloadable"), node.Name);
                return;
            }

            // The temp file needs the extension so the OS picks the right application.
            await NativeFileOpener.OpenAsync(preview.DownloadUrl, WithExtension(node.Name, preview.FileExtension));
            Status = string.Format(Strings.Get("StOpenedNative"), node.Name);
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrOpen2"), node.Name, e.Message);
        }
    }

    // Browses a .zip's entries virtually — read on demand, nothing unpacked (ADR "Zip file browsing"). The
    // list is replaced with a "back" row + one row per entry; the breadcrumb/current folder are left as-is so
    // exiting returns to them.
    private async Task EnterArchiveAsync(NodeViewModel zip)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            // `archive-entries` is CONDITIONAL on the resource — its presence is the server answering "can I
            // browse inside this?" — so it is resolved through the row's document address (ADR 0559).
            var entries = await _api.Documents.GetArchiveEntriesAsync(await _api.Documents.RelViaSelfAsync(zip.DocumentSelfHref, "archive-entries"));
            _archiveDocumentId = zip.Id;
            CanCreateFolder = false;
            CanExport = false;

            Items.Clear();
            Items.Add(new NodeViewModel { Id = Guid.Empty, Name = $"⬆ {zip.Name}", HasChildren = false, HasVersions = false, IsArchiveBack = true });
            foreach (var entry in entries)
            {
                Items.Add(new NodeViewModel
                {
                    Id = Guid.Empty,
                    Name = entry.Path,
                    HasChildren = false,
                    HasVersions = true,
                    IsArchiveEntry = true,
                    ArchiveEntryPath = entry.Path,
                    ArchiveEntryDownloadHref = entry.DownloadHref,
                });
            }

            Status = string.Format(Strings.Get("StArchiveEntries"), entries.Count);
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Get("StErrRead2"), zip.Name, ex.Message);
        }
    }

    private async Task ExitArchiveAsync()
    {
        if (_currentFolderId is { } folderId)
        {
            await LoadFolderContentsAsync(folderId); // clears _archiveDocumentId and re-lists the folder
        }
        else
        {
            _archiveDocumentId = null;
        }
    }

    // Downloads one archive entry's bytes for the Save-as flow (the view picks the destination).
    public async Task<byte[]?> DownloadArchiveEntryAsync(NodeViewModel entry)
    {
        if (_api is null || entry.ArchiveEntryDownloadHref is not { } href)
        {
            return null;
        }

        return await _api.DownloadArchiveEntryAsync(href);
    }

    // Resolves the latest confirmed version's download URL for a document (the view then shows a Save-as
    // dialog and writes the bytes to the chosen location). Null if there's no downloadable version.
    // The presigned download URL plus a suggested filename = Document.Name (the stem) + the version's file
    // extension (ADR "Extension off Document.Name"), so Save-as writes e.g. "scan.tif", not the extension-less
    // "scan".
    public async Task<(string? Url, string FileName)> GetDownloadInfoAsync(NodeViewModel node)
    {
        if (_api is null || node.IsFolder)
        {
            return (null, node.Name);
        }

        var preview = await _api.Documents.GetPreviewAsync(node.Href("versions"));
        return (preview.DownloadUrl, WithExtension(node.Name, preview.FileExtension));
    }

    // Reconstructs a filename from Document.Name (a bare stem, ADR "Extension off Document.Name") + the
    // version's extension — but only appends when the name doesn't already carry it, so pre-extension-change
    // data (whose Name still includes the extension) doesn't get a doubled ".zip.zip".
    internal static string WithExtension(string name, string extension) =>
        string.IsNullOrEmpty(extension) || name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + extension;

    // Creates a folder in the currently-open folder (the view collects the name via a dialog and calls this).
    public async Task CreateFolderAsync(string name)
    {
        if (_api is null || _currentFolderId is not { } folderId || _currentFolderLinks is not { } folderLinks)
        {
            return;
        }

        try
        {
            await _api.Documents.CreateFolderAsync(folderLinks["children"], name);
            Status = string.Format(Strings.Get("StCreatedFolder"), name);
            await ShowNewChildInTreeAsync(folderId); // refresh the parent's children in the tree, keep it expanded
            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrCreateFolder"), e.Message);
        }
    }

    // ---- Tree-pane folder actions (the code-behind context menu targets a tree node by id) ----------------

    // Create a subfolder directly under a tree folder (not necessarily the currently-open one) — through the
    // node's own children address (ADR 0555); the id only drives the local tree refresh.
    public async Task CreateSubfolderAsync(Guid parentId, string childrenHref, string name)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.CreateFolderAsync(childrenHref, name);
            Status = string.Format(Strings.Get("StCreatedFolder"), name);
            await ShowNewChildInTreeAsync(parentId);
            if (_currentFolderId == parentId)
            {
                await LoadFolderContentsAsync(parentId);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrCreateFolder"), e.Message); }
    }

    // Rename a tree folder by id (rebuilds the tree so its node label updates, unlike the list-row rename).
    public async Task RenameFolderAsync(string documentSelfHref, string newName)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.RenameAsync(documentSelfHref, newName);
            Status = string.Format(Strings.Get("StRenamedTo"), newName);
            await ReloadTreeAsync();
            if (_currentFolderId is { } current)
            {
                await LoadFolderContentsAsync(current);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrRename"), e.Message); }
    }

    // Move a TREE folder (and its subtree) under another folder, by id — the tree context menu's "Move to…"
    // (ADR "Tree-pane context menu"). Unlike MoveNodeAsync (a dragged contents-list row) the tree itself
    // changes shape, so this reloads the tree as well as the open folder's contents.
    public async Task MoveFolderAsync(string documentSelfHref, string folderName, Guid targetFolderId)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.MoveAsync(documentSelfHref, targetFolderId);
            Status = string.Format(Strings.Get("StMoved"), folderName);
            await ReloadTreeAsync();
            if (_currentFolderId is { } current)
            {
                await LoadFolderContentsAsync(current);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrMove"), e.Message); }
    }

    // Place a reference (shortcut) to a TREE folder into another folder, by id — the tree context menu's
    // "Place reference…". The referenced folder shows up in the target's subtree (ADR "Referenced folder in the
    // tree"), so the tree is reloaded too.
    public async Task PlaceReferenceAsync(Guid folderId, string folderName, string targetReferencesHref)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.CreateReferenceAsync(targetReferencesHref, folderId);
            Status = string.Format(Strings.Get("StPlacedRef"), folderName);
            await ReloadTreeAsync();
            if (_currentFolderId is { } current)
            {
                await LoadFolderContentsAsync(current);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrPlaceRef"), e.Message); }
    }

    // Soft-delete a tree folder (and its subtree) to the recycle bin by id.
    public async Task DeleteFolderAsync(Guid folderId, string documentSelfHref)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.DeleteAsync(documentSelfHref);
            Status = Strings.Get("StFolderDeleted");
            if (_currentFolderId == folderId)
            {
                _currentFolderId = null;
                Items.Clear();
                ClearDetail();
            }

            await ReloadTreeAsync();
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrDeleteMsg"), e.Message); }
    }

    // Follow / unfollow a folder and its whole subtree (ADR "Folder / subtree subscriptions") — fetches the
    // current state and toggles it, so one menu item is always correct.
    public async Task ToggleFolderSubscriptionAsync(string documentSelfHref)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            // ONE fetch of the folder's resource, then both halves follow its advertised address (ADR 0557).
            var subscriptionHref = await _api.Documents.RelViaSelfAsync(documentSelfHref, "subscription");
            var following = await _api.Documents.GetSubscriptionAsync(subscriptionHref);
            await _api.Documents.SetSubscriptionAsync(subscriptionHref, !following);
            Status = !following ? "Following this folder and everything in it." : "Unfollowed folder.";
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrSubscriptionMsg"), e.Message); }
    }

    // Renames a document/folder (the view collects the new name via a dialog and calls this). Reloads the
    // current folder so the new name shows. A renamed sub-folder's tree node stays stale until a refresh —
    // same whole-tree-reload simplification as upload.
    public async Task RenameNodeAsync(NodeViewModel node, string newName)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            return;
        }

        try
        {
            await _api.Documents.RenameAsync(node.DocumentSelfHref, newName);
            Status = string.Format(Strings.Get("StRenamedTo"), newName);
            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrRename"), e.Message);
        }
    }

    // Soft-deletes a document/folder to the recycle bin (the view confirms first and calls this). For a
    // reference row, removes only the shortcut (never the target) — see ADR "Desktop drag-and-drop move
    // and reference".
    public async Task DeleteNodeAsync(NodeViewModel node)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            return;
        }

        try
        {
            // Branch on WHAT THE ROW IS first, and only then on whether it gave us an address. Folding the
            // href test into the `if` narrows it — and silently widens the `else`, which deletes the target
            // DOCUMENT. A rel may legitimately be absent (ADR 0543), so "reference without a delete address"
            // is a state that has to be handled, never one that falls through to a more destructive action.
            if (node.IsReference)
            {
                if (node.ReferenceDeleteHref is not { } referenceDeleteHref)
                {
                    Status = string.Format(Strings.Get("StErrDeleteMsg"), $"'{node.Name}' offered no way to remove the shortcut.");
                    return;
                }

                await _api.Documents.DeleteReferenceAsync(referenceDeleteHref);
                Status = string.Format(Strings.Get("StRemovedRef"), node.Name);
            }
            else
            {
                await _api.Documents.DeleteAsync(node.DocumentSelfHref);
                if (_selectedDocumentId == node.Id)
                {
                    ClearDetail();
                }

                Status = string.Format(Strings.Get("StDeleted"), node.Name);
            }

            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrDeleteMsg"), e.Message);
        }
    }

    // Moves (reparents) a dragged item into a folder (the view collects Move-vs-reference and calls this).
    public async Task MoveNodeAsync(NodeViewModel node, Guid targetFolderId)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            return;
        }

        try
        {
            await _api.Documents.MoveAsync(node.DocumentSelfHref, targetFolderId);
            Status = string.Format(Strings.Get("StMoved"), node.Name);
            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrMove"), e.Message);
        }
    }

    // ---- Bulk actions on the multi-selection (ADR "Bulk actions on selected documents") ------------------
    [ObservableProperty] private bool _hasBulkSelection;
    [ObservableProperty] private int _bulkSelectionCount;
    private List<NodeViewModel> _bulkSelection = [];

    // Called by the view's SelectionChanged: the current multi-selection (references / archive rows excluded).
    // The bulk-action bar shows when ≥2 real items are selected.
    public void SetBulkSelection(IEnumerable<NodeViewModel> selected)
    {
        _bulkSelection = selected.Where(n => !n.IsReference && !n.IsArchiveEntry && !n.IsArchiveBack).ToList();
        BulkSelectionCount = _bulkSelection.Count;
        HasBulkSelection = _bulkSelection.Count >= 2;
    }

    // A pure folder picker (no filing options) for choosing a bulk-move target.
    public FolderPickerViewModel CreateMoveTargetPickerViewModel() =>
        _api is null ? null! : new FolderPickerViewModel(_api, null, bulk: true);

    // The tenant tag catalog, for the bulk add-tags dialog's autocomplete.
    public async Task<IReadOnlyList<string>> GetTagCatalogAsync()
    {
        if (_api is null)
        {
            return [];
        }

        try { return await _api.Documents.GetTagCatalogAsync(); } catch (Exception) { return []; }
    }

    public Task BulkMoveAsync(Guid targetFolderId) =>
        RunBulkAsync(ids => _api!.Documents.BulkMoveAsync(ids, targetFolderId), "moved");

    public Task BulkDeleteAsync() =>
        RunBulkAsync(ids => _api!.Documents.BulkDeleteAsync(ids), "deleted");

    public Task BulkAddTagsAsync(IReadOnlyList<string> tags) =>
        RunBulkAsync(ids => _api!.Documents.BulkAddTagsAsync(ids, tags), "tagged");

    public Task BulkSetSensitivityAsync(Guid? labelId) =>
        RunBulkAsync(ids => _api!.Documents.BulkSetSensitivityAsync(ids, labelId), "classified");

    private async Task RunBulkAsync(Func<IReadOnlyList<Guid>, Task<BulkResult>> action, string verb)
    {
        if (_api is null || _currentFolderId is not { } folderId || _bulkSelection.Count == 0)
        {
            return;
        }

        var ids = _bulkSelection.Select(n => n.Id).ToList();
        try
        {
            var result = await action(ids);
            Status = string.Format(Strings.Get("StBulkResult"), result.Succeeded, verb) + (result.Skipped > 0 ? string.Format(Strings.Get("StBulkSkipped"), result.Skipped) : ".");
            SetBulkSelection([]);
            ClearDetail();
            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrBulk"), e.Message);
        }
    }

    // Files a reference (shortcut) to a dragged item into a folder. node.Id is the target (for a reference
    // source it's the underlying item, so referencing a reference just points at the same item).
    public async Task ReferenceNodeAsync(NodeViewModel node, string targetReferencesHref)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            return;
        }

        try
        {
            await _api.Documents.CreateReferenceAsync(targetReferencesHref, node.Id);
            Status = string.Format(Strings.Get("StPlacedRef"), node.Name);
            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrPlaceRef"), e.Message);
        }
    }

    // Move / reference a specific set of dragged item ids into a target folder — used by drag-drop, which operates
    // on the DRAGGED selection (which may differ from the persisted multi-selection that RunBulkAsync uses).
    public Task BulkMoveNodesAsync(IReadOnlyList<Guid> ids, Guid targetFolderId) =>
        RunDroppedBulkAsync(() => _api!.Documents.BulkMoveAsync(ids, targetFolderId), "moved", ids.Count);

    public Task BulkReferenceNodesAsync(IReadOnlyList<Guid> ids, Guid targetFolderId) =>
        RunDroppedBulkAsync(() => _api!.Documents.BulkReferenceAsync(ids, targetFolderId), "referenced", ids.Count);

    private async Task RunDroppedBulkAsync(Func<Task<BulkResult>> action, string verb, int count)
    {
        if (_api is null || _currentFolderId is not { } folderId || count == 0)
        {
            return;
        }

        try
        {
            var result = await action();
            Status = string.Format(Strings.Get("StBulkResult"), result.Succeeded, verb) + (result.Skipped > 0 ? string.Format(Strings.Get("StBulkSkipped"), result.Skipped) : ".");
            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrBulk"), e.Message);
        }
    }

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
            Status = string.Format(Strings.Get("StErrOpenFolder"), e.Message);
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
            Status = e.Message;
        }
    }

    // ---- Intray (ADR "S3-backed inbox", phase 2) -------------------------------------------------------

    // Still used by the Check-out tab (local working-copy folder) + native-open temp dir; the local INTRAY half
    // was removed in favour of the WebDAV mount (ADR "Desktop inbox via WebDAV").
    private LocalFolders? _localFolders;
    public ObservableCollection<IntrayItemViewModel> ServerIntray { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedIntrayItem))]
    [NotifyPropertyChangedFor(nameof(CanSendSelectedIntrayItem))]
    [NotifyPropertyChangedFor(nameof(CanClaimSelectedIntrayItem))]
    private IntrayItemViewModel? _selectedServerIntrayItem;

    // What the Intray ribbon's selected-item group is gated on (#521). Greyed rather than hidden when there is
    // no selection: the button is available to this user, it simply has nothing to act on yet, and hiding it
    // would make the ribbon reflow under the cursor as the selection changes (the Repositories tab's rule).
    public bool HasSelectedIntrayItem => SelectedServerIntrayItem is not null;

    /// <summary>"Send to…" is for your OWN item; somebody else's is claimed instead (ADR 0532).</summary>
    public bool CanSendSelectedIntrayItem => SelectedServerIntrayItem?.IsOwn == true;

    /// <summary>And the mirror: a group or other-user item is the one you can move to your own intray.</summary>
    public bool CanClaimSelectedIntrayItem => SelectedServerIntrayItem is { IsOwn: false };
    [ObservableProperty] private string _intrayStatus = "";

    // After login: resolve the tenant/user display names and create the local ~/SimplArchive/{Tenant}/{User}/
    // {intray,temp} folders; point native-open at the temp folder.
    private async Task SetupUserContextAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var me = await _api.GetWhoAmIAsync();
            IsTenantAdmin = me.IsTenantAdmin;
            RecycleBin.IsTenantAdmin = me.IsTenantAdmin;
            CanManageUsers = me.CanManageUsers;
            CanManageServiceAccounts = me.CanManageServiceAccounts;
            CanViewAuditLog = me.CanViewAuditLog;
            MfaEnabled = me.MfaEnabled;
            CanResetMfa = me.CanResetMfa;
            CanLegalHold = me.CanLegalHold;
            CanManageClassification = me.CanManageClassification;
            CanOverrideCheckout = me.CanOverrideCheckout;
            CanImpersonate = me.CanImpersonate;
            HasExportRight = me.CanExport;
            HasImportRight = me.CanImport;
            CanManageIntrayes = me.CanManageIntrayes;
            IsImpersonating = me.ImpersonatedBy is not null;
            ImpersonatedName = me.ImpersonatedBy is not null ? me.UserName : null;
            _currentUserId = me.UserId;
            UserDisplayName = me.UserName ?? "";
            await LoadMyPhotoAsync();
            if (me.TenantName is { } tenantName && me.UserName is { } userName)
            {
                _localFolders = new LocalFolders(tenantName, userName);
                NativeFileOpener.TempDirectoryOverride = _localFolders.TempDirectory;
                Checkout.Setup(_api);
            }

            await LoadTasksAsync(); // for the Tasks tab count badge
            await LoadNotificationsAsync(); // for the notifications bell badge
            await StartRealtimeNotificationsAsync(); // live bell updates (ADR "Real-time notifications (SignalR)")
            await LoadSensitivityCatalogAsync(); // the tenant's sensitivity labels for the picker + admin
            // The Check-out tab reads its modified state from the server (ADR 0513) — no local working copy to restore.
            await Checkout.LoadAsync();
            OnPropertyChanged(nameof(CheckoutCount));
            OnPropertyChanged(nameof(HasCheckouts));
        }
        catch (Exception)
        {
            // Non-fatal — the intray still works without the local folders.
        }
    }

    // Tenant-admin-only actions (e.g. the searchable-PDF backfill) are gated on this, set from whoami on login.
    [ObservableProperty] private bool _isTenantAdmin;

    // Whether the caller may force-release another user's check-out (ADR "Document check-out / check-in") — gates
    // the "Override check-out" context-menu action; set from whoami on login.
    [ObservableProperty] private bool _canOverrideCheckout;

    // Whether the caller holds CanExport / CanImport (ADR "Dedicated CanExport/CanImport rights") — gates the
    // visibility of the ribbon Export…/Import… buttons (replacing the old tenant-admin gate). Distinct from the
    // CanExport property above, which is the folder-open enabled-state of the Export button.
    [ObservableProperty] private bool _hasExportRight;

    [ObservableProperty] private bool _hasImportRight;

    // Whether the caller holds CanManageIntrayes (own or via a group) — gates the intray user-picker that opens
    // another user's intray for triage (ADR 0532); set from whoami on login.
    [ObservableProperty] private bool _canManageIntrayes;

    // User impersonation (ADR "User impersonation"): CanImpersonate gates the "Impersonate" action; while
    // IsImpersonating, a banner shows ImpersonatedName + a Stop button. _adminApi is the pre-impersonation client
    // kept so Stop can revert. Impersonation is one level (started only from the admin's own session).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanImpersonateSelectedPrincipal))] private bool _canImpersonate;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanImpersonateSelectedPrincipal))] private bool _isImpersonating;
    [ObservableProperty] private string? _impersonatedName;
    private SimplArchiveApiClient? _adminApi;

    // Impersonate a target user: exchange the admin token (RFC 8693), swap the api client, and reload the
    // workbench as that user. The server enforces the rules (non-admin, active, same tenant).
    public async Task ImpersonateAsync(Guid targetUserId)
    {
        if (_api is not { } api || IsImpersonating)
        {
            return;
        }

        var token = await SimplArchiveApiClient.ExchangeImpersonationTokenAsync(api.AccessToken, targetUserId);
        if (token is null)
        {
            Status = Strings.Get("StErrImpersonate");
            return;
        }

        _adminApi = api;
        UseApi(new SimplArchiveApiClient(token));
        await SetupUserContextAsync();
        await LoadRootAsync();
    }

    [RelayCommand]
    private async Task StopImpersonating()
    {
        if (_adminApi is not { } admin)
        {
            return;
        }

        UseApi(admin);
        _adminApi = null;
        await SetupUserContextAsync();
        await LoadRootAsync();
    }

    // Check-out tab count badge.
    public int CheckoutCount => Checkout.Count;

    public bool HasCheckouts => Checkout.Count > 0;

    // Check out the selected document: take the lock + download the current version into the local checkout
    // folder for editing. Enabled for a document row that isn't already checked out.
    public bool CanCheckOut => SelectedItem is { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false, CheckedOut: false };

    // Override a document checked out by someone else (a CanOverrideCheckout holder force-releases the lock).
    public bool CanOverrideSelected => CanOverrideCheckout && SelectedItem is { CheckedOut: true, CheckedOutByMe: false };

    [RelayCommand]
    private async Task CheckOutSelectedAsync()
    {
        if (_api is null || SelectedItem is not { } item || !CanCheckOut)
        {
            return;
        }

        Status = string.Format(Strings.Get("StCheckingOut"), item.Name);
        try
        {
            await _api.Checkout.CheckOutViaDocumentAsync(item.Href("self"));
            // The lock is acquired server-side; editing happens via the WebDAV mount (ADR 0513) — no local copy.
            Status = string.Format(Strings.Get("StCheckedOut"), item.Name);
            await RefreshAfterCheckoutChangeAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrCheckout2"), item.Name, e.Message);
        }
    }

    [RelayCommand]
    private async Task OverrideCheckoutSelectedAsync()
    {
        if (_api is null || SelectedItem is not { } item || !CanOverrideSelected)
        {
            return;
        }

        try
        {
            await _api.Checkout.CheckInViaDocumentAsync(item.Href("self")); // force-release (override)
            Status = string.Format(Strings.Get("StReleasedCheckout"), item.Name);
            await RefreshAfterCheckoutChangeAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrOverride2"), item.Name, e.Message);
        }
    }

    // The current version's file extension for a document (so the working copy keeps the right type) —
    // read from the versions address the caller's row advertised (ADR 0555).
    private async Task<string> ResolveFileExtensionAsync(string versionsHref)
    {
        if (_api is null)
        {
            return "";
        }

        var fields = await _api.Documents.GetSystemFieldsAsync(versionsHref);
        return fields?.FileExtension ?? "";
    }

    // After a check-out/check-in/override changes lock state: reload the open folder's list (lock glyphs) and
    // the Check-out tab count.
    private async Task RefreshAfterCheckoutChangeAsync()
    {
        if (_currentFolderId is { } folderId && _archiveDocumentId is null)
        {
            var selectedId = SelectedItem?.Id;
            await LoadFolderContentsAsync(folderId);
            if (selectedId is { } id && Items.FirstOrDefault(n => n.Id == id) is { } fresh)
            {
                SelectedItem = fresh;
            }
        }

        OnPropertyChanged(nameof(CheckoutCount));
        OnPropertyChanged(nameof(HasCheckouts));
    }

    // Reconnect action for the crash-guard dialog (ADR "Desktop crash guard"): re-check the session and reload
    // the current view. May throw again if still offline — the dialog swallows that rather than looping.
    public async Task ReconnectAsync()
    {
        if (_api is null)
        {
            return;
        }

        await SetupUserContextAsync();
        await LoadRootAsync();
        if (_currentFolderId is { } folderId)
        {
            await LoadFolderContentsAsync(folderId);
        }

        Status = Strings.Get("StReconnected");
    }

    // How many existing "current TIFF" documents still need a searchable-PDF successor (ADR "Backfill
    // searchable PDFs for existing TIFFs") — the view confirms with this before triggering.
    public async Task<int> GetTiffBackfillPendingAsync() => _api is null ? 0 : await _api.GetTiffBackfillPendingAsync();

    // Triggers the backfill and reports the count queued.
    public async Task RunTiffBackfillAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var count = await _api.TriggerTiffBackfillAsync();
            Status = count == 0
                ? "No documents needed conversion."
                : $"Queued {count} document(s) for searchable-PDF conversion.";
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrStartConversion"), e.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshIntrayAsync()
    {
        if (_api is null)
        {
            return;
        }

        ServerIntray.Clear();
        try
        {
            // The admin user-picker's choices (CanManageIntrayes only) — loaded once, "My intray" first (null id).
            if (CanManageIntrayes && IntrayUsers.Count == 0)
            {
                IntrayUsers.Add(new IntrayUserPickerItem(null, Strings.Get("IntrayMine")));
                foreach (var u in await _api.Intray.GetIntrayUsersAsync())
                {
                    IntrayUsers.Add(new IntrayUserPickerItem(u.Id, u.Name));
                }
            }

            // One read, many follows (ADR 0557): the listing carries the rows AND the collection's own `join`
            // address, so the Join action costs no extra request when a selection later enables it.
            var listing = await _api.Intray.ListAsync(IntrayIncludeGroups, IntrayViewUserId);
            IntrayActions.JoinHref = listing.Href("join");
            IntrayActions.PatchCodeSheetHref = listing.Href("patchCodeSheet");
            foreach (var item in listing.Items)
            {
                ServerIntray.Add(new IntrayItemViewModel
                {
                    Name = item.Name,
                    Size = item.Size,
                    DownloadUrl = item.DownloadUrl,
                    HasMask = item.HasMask,
                    GroupId = item.GroupId,
                    GroupName = item.GroupName,
                    UserId = item.UserId,
                    UserName = item.UserName,
                    MoveUrl = item.MoveUrl,
                    IsSigned = item.Signed,
                    Item = item,
                });
            }
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Get("StErrLoadIntray"), ex.Message);
        }

        IntrayStatus = string.Format(Strings.Get("StItems"), ServerIntray.Count);

        // A refresh rebuilds the list, so nothing is focused — clear the right panes.
        SelectedServerIntrayItem = null;
    }

    // ---- Intray view filters (ADR 0532): own-items-only by default; a toggle reveals group intrayes, and a
    // CanManageIntrayes holder can open a specific user's intray via the picker (mutually exclusive with groups). ----

    [ObservableProperty] private bool _intrayIncludeGroups;
    [ObservableProperty] private Guid? _intrayViewUserId;

    // The user-picker choices (only populated for a CanManageIntrayes holder); the first is "My intray" (null id).
    public ObservableCollection<IntrayUserPickerItem> IntrayUsers { get; } = [];

    [ObservableProperty] private IntrayUserPickerItem? _selectedIntrayUser;

    // Suppresses the reentrant refresh when one filter handler adjusts the other (the two are mutually exclusive).
    private bool _adjustingIntrayFilters;

    // The "Show group intrayes" checkbox — reveals my group intrayes; clears any admin user-view (they're exclusive).
    async partial void OnIntrayIncludeGroupsChanged(bool value)
    {
        if (_adjustingIntrayFilters)
        {
            return;
        }

        _adjustingIntrayFilters = true;
        if (value)
        {
            IntrayViewUserId = null;
            SelectedIntrayUser = IntrayUsers.Count > 0 ? IntrayUsers[0] : null; // back to "My intray"
        }

        _adjustingIntrayFilters = false;
        await RefreshIntrayAsync();
    }

    // The admin user-picker — open a chosen user's intray, or (null id) back to my own.
    async partial void OnSelectedIntrayUserChanged(IntrayUserPickerItem? value)
    {
        if (_adjustingIntrayFilters)
        {
            return;
        }

        _adjustingIntrayFilters = true;
        IntrayViewUserId = value?.UserId;
        if (value is { UserId: not null })
        {
            IntrayIncludeGroups = false;
        }

        _adjustingIntrayFilters = false;
        await RefreshIntrayAsync();
    }

    // Upload OS files dropped onto the intray file-list straight into the S3-backed intray (ADR "Inbox file-list
    // drop-zone"). The view reads each dropped file into (name, bytes); this uploads them, then refreshes.
    // Drops onto the Personal ▸ Intray / Check-out tree launchers (#467). The work is in DropFiling — this class
    // is already the largest entry on the 1000-line debt list (#466) — and what stays here is what only the
    // view-model can do: own the status line and know what to refresh.
    private DropFiling? _dropFiling;

    public async Task CopyDocumentsToIntrayAsync(IReadOnlyList<Guid> documentIds)
    {
        if (_api is not { } api)
        {
            return;
        }

        _dropFiling ??= new DropFiling(api);
        if (await _dropFiling.CopyToIntrayAsync(documentIds, message => Status = message) > 0)
        {
            await RefreshIntrayAsync();
            SelectedTab = 1;   // the Intray tab: the tree lists FOLDERS and can never show what just landed
        }
    }

    public async Task StashDroppedFilesAsync(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        if (_api is not { } api)
        {
            return;
        }

        _dropFiling ??= new DropFiling(api);
        var items = Checkout.Items.Select(r => r.Item).OfType<CheckoutClient.CheckoutItem>().ToList();
        if (await _dropFiling.StashAsync(files, items, message => Status = message) > 0)
        {
            await Checkout.LoadAsync();
        }
    }

    public async Task UploadFilesToIntrayAsync(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        if (_api is null || files.Count == 0)
        {
            return;
        }

        IntrayStatus = string.Format(Strings.Get("StUploadingN"), files.Count);
        var uploaded = 0;
        foreach (var (name, bytes) in files)
        {
            try
            {
                await _api.Intray.UploadAsync(name, bytes);
                uploaded++;
            }
            catch (Exception ex)
            {
                Status = string.Format(Strings.Get("StErrUpload2b"), name, ex.Message);
            }
        }

        await RefreshIntrayAsync();
        if (uploaded > 0)
        {
            IntrayStatus = string.Format(Strings.Get("StUploadedAndItems"), uploaded, ServerIntray.Count);
        }
    }

    // "Open in file manager" + "WebDAV settings" (Intray tab) now live in the code-behind (MainWindow.axaml.cs
    // OnOpenWebDavIntray / OnManageWebDav), since opening the settings dialog when WebDAV isn't configured needs
    // the Window (ADR "Desktop inbox WebDAV buttons"). The mount logic stays in Services/OsFileManager.

    // ---- Intray item detail (right panes): a mask/index-data editor + the shared preview -------------------
    // The panes are driven by the focused server item; the mask edits are staged to a `{name}.mask.json`
    // sidecar (ADR "Inbox item classification + preview"). The mask pane is only editable for a server item.

    [ObservableProperty] private bool _intrayItemFocused;
    [ObservableProperty] private bool _intrayIsEmail; // .eml/.msg → classified by the system, no mask offered
    [ObservableProperty] private string _intrayDetailTitle = "";
    [ObservableProperty] private string _intrayName = "";
    [ObservableProperty] private DateTime? _intrayDocumentDate;
    [ObservableProperty] private MaskChoiceViewModel? _intraySelectedMaskChoice;

    public ObservableCollection<MaskChoiceViewModel> IntrayAvailableMasks { get; } = [];
    public ObservableCollection<MaskFieldEditViewModel> IntrayMaskEditFields { get; } = [];

    private Dictionary<Guid, IReadOnlyList<string>> _intrayDraftValues = [];
    private bool _loadingIntrayMask;

    // Loads the right panes when a server intray item gains focus (or clears them when focus is lost).
    async partial void OnSelectedServerIntrayItemChanged(IntrayItemViewModel? value)
    {
        if (value is null)
        {
            ClearIntrayDetail();
            return;
        }

        if (_api is null)
        {
            return;
        }

        IntrayDetailTitle = value.Name;
        IntrayPreview.Reset("Loading…");
        IntrayPreview.FindQuery = "";

        // An email (.eml/.msg) is classified automatically by the system when filed — the mask isn't offered
        // in the intray for it (ADR "Consume the staged mask sidecar at filing").
        var extension = Path.GetExtension(value.Name);
        IntrayIsEmail = extension.Equals(".eml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msg", StringComparison.OrdinalIgnoreCase);

        try
        {
            await IntrayPreview.RenderAsync(await _api.Intray.GetIntrayPreviewAsync(value.Item!));
            if (!IntrayIsEmail)
            {
                await LoadIntrayMaskAsync(value.Item!);
            }

            IntrayItemFocused = true;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad2"), value.Name, e.Message);
        }
    }

    private async Task LoadIntrayMaskAsync(IntrayApi.IntrayItemInfo item)
    {
        _loadingIntrayMask = true;
        try
        {
            IntrayAvailableMasks.Clear();
            IntrayAvailableMasks.Add(new MaskChoiceViewModel(null, "(No mask)"));
            foreach (var mask in await _api!.Documents.GetMasksAsync())
            {
                IntrayAvailableMasks.Add(new MaskChoiceViewModel(mask.Id, mask.Name, mask));
            }

            var name = item.Name;
            var draft = await _api.Intray.GetIntrayMaskAsync(item);
            _intrayDraftValues = draft.Fields.ToDictionary(f => f.FieldDefinitionId, f => f.Values);
            IntrayName = string.IsNullOrEmpty(draft.Name) ? Path.GetFileNameWithoutExtension(name) : draft.Name;
            IntrayDocumentDate = DateTime.TryParse(draft.DocumentDate, out var d) ? d.Date : null;

            // OCR languages: only for a scannable item, staged + applied at filing (ADR "Inbox OCR-language
            // staging"). Load the catalog on demand so DescribeOcrLanguages can map codes → names.
            IntrayStgScannable = IsScannableExtension(name);
            _intrayStgOcrCodes = draft.OcrLanguages.ToList();
            if (IntrayStgScannable && _ocrCatalog.Count == 0)
            {
                try { _ocrCatalog = await _api.GetOcrLanguageCatalogAsync(); }
                catch { /* non-fatal — the picker just shows codes */ }
            }
            IntrayOcrDisplay = DescribeOcrLanguages(_intrayStgOcrCodes);

            // Preselect the staged mask, or default to "Basic Entry" for an un-classified item (the same
            // default auto-classification applies at filing).
            IntraySelectedMaskChoice = draft.MaskId is { } staged
                ? IntrayAvailableMasks.FirstOrDefault(m => m.MaskId == staged) ?? IntrayAvailableMasks[0]
                : IntrayAvailableMasks.FirstOrDefault(m => m.Name == "Basic Entry") ?? IntrayAvailableMasks[0];
            await LoadIntrayMaskFieldsAsync(IntraySelectedMaskChoice?.Mask, useDraftValues: true);
        }
        finally
        {
            _loadingIntrayMask = false;
        }
    }

    // Reloads the field editors when a different mask is picked (empty values); suppressed on the initial load,
    // which fills the staged draft values instead.
    async partial void OnIntraySelectedMaskChoiceChanged(MaskChoiceViewModel? value)
    {
        if (_loadingIntrayMask)
        {
            return;
        }

        await LoadIntrayMaskFieldsAsync(value?.Mask, useDraftValues: false);
    }

    private async Task LoadIntrayMaskFieldsAsync(DocumentsClient.MaskOptionInfo? mask, bool useDraftValues)
    {
        IntrayMaskEditFields.Clear();
        if (_api is null || mask is not { } chosen)
        {
            return;
        }

        foreach (var field in await _api.Documents.GetMaskFieldsAsync(chosen))
        {
            var values = useDraftValues && _intrayDraftValues.TryGetValue(field.Id, out var v) ? v : [];
            IntrayMaskEditFields.Add(MaskFieldEditViewModel.Create(field, values));
        }
    }

    // Saves the staged mask/index-data to the focused item's `{name}.mask.json` sidecar (no filed Document yet,
    // so no required-field validation runs here). Updates the item's square-bracket indicator in place.
    [RelayCommand]
    private async Task SaveIntrayMaskAsync()
    {
        if (_api is null || SelectedServerIntrayItem is not { } item)
        {
            return;
        }

        try
        {
            var maskId = IntraySelectedMaskChoice?.MaskId;
            var fields = IntrayMaskEditFields.Select(f => (f.FieldDefinitionId, f.ToValues())).ToList();
            var stagedName = string.IsNullOrWhiteSpace(IntrayName) ? null : IntrayName.Trim();
            var docDate = IntrayDocumentDate?.ToString("yyyy-MM-dd");
            var ocr = IntrayStgScannable && _intrayStgOcrCodes.Count > 0 ? _intrayStgOcrCodes : null;
            await _api.Intray.SetIntrayMaskAsync(item.Item!, stagedName, docDate, maskId, fields, ocr);
            item.HasMask = maskId is not null || fields.Any(f => f.Item2.Count > 0) || stagedName is not null || docDate is not null || ocr is not null;
            Status = Strings.Get("StMaskSaved");
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrSaveMask"), e.Message);
        }
    }

    // The intray mask pane's OCR-language picker (ADR "Inbox OCR-language staging") — shown only for a scannable
    // item (.tif/.tiff/.pdf); edited via the view's OnEditIntrayOcrLanguages (the shared OcrLanguagePickerDialog),
    // staged into the pane, and consumed at filing to OCR the searchable-PDF successor in the chosen languages.
    [ObservableProperty] private bool _intrayStgScannable;
    [ObservableProperty] private string _intrayOcrDisplay = "";
    private List<string> _intrayStgOcrCodes = [];

    public (IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> Catalog, IReadOnlyList<string> Selected) IntrayOcrPickerState() =>
        (_ocrCatalog, _intrayStgOcrCodes);

    public void StageIntrayOcrLanguages(IReadOnlyList<string> codes)
    {
        _intrayStgOcrCodes = codes.ToList();
        IntrayOcrDisplay = DescribeOcrLanguages(_intrayStgOcrCodes);
    }

    private static bool IsScannableExtension(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".tif" or ".tiff" or ".pdf";

    private void ClearIntrayDetail()
    {
        IntrayItemFocused = false;
        IntrayIsEmail = false;
        IntrayDetailTitle = "";
        IntrayName = "";
        IntrayDocumentDate = null;
        IntrayAvailableMasks.Clear();
        IntrayMaskEditFields.Clear();
        _intrayDraftValues = [];
        IntrayPreview.Reset("Select a server intray item.");
        IntrayPreview.PreviewConverted = false;
        IntrayPreview.CanFindInDocument = false;
        IntrayPreview.FindQuery = "";
    }

    // Opens a server intray item natively: download it to the temp folder, then hand it to its OS app.
    [RelayCommand]
    private async Task OpenServerIntrayItemAsync()
    {
        if (SelectedServerIntrayItem is not { } item)
        {
            return;
        }

        try
        {
            await NativeFileOpener.OpenAsync(item.DownloadUrl, item.Name);
            Status = string.Format(Strings.Get("StOpened"), item.Name);
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Get("StErrOpen2"), item.Name, ex.Message);
        }
    }

    // Files a server intray item into a chosen folder (the view picks it), then refreshes.
    public async Task FileServerIntrayItemAsync(IntrayItemViewModel item, Guid folderId, string? comment)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Intray.FileIntrayItemAsync(item.Item!, folderId, comment);
            Status = string.Format(Strings.Get("StFiled"), item.Name);
            await RefreshIntrayAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }

    // Files a server intray item as a new version of an existing document (ADR "Context-aware inbox filing dialog").
    public async Task FileServerIntrayItemAsVersionAsync(IntrayItemViewModel item, Guid documentId, string? comment)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Intray.FileIntrayItemAsVersionAsync(item.Item!, documentId, comment);
            Status = string.Format(Strings.Get("StFiledVersion"), item.Name);
            await RefreshIntrayAsync();

            // The server posts a feed comment on the filed document and adds a new version (ADR "Filing posts a
            // feed comment"). If that document is the one currently open on the Repositories tab, refresh its
            // detail so the comment + the new version's preview show without a manual reselect.
            if (_selectedDocumentId == documentId)
            {
                await LoadCommentsAsync(DetailHref("chat"));
                await LoadPreviewAsync(DetailHref("versions"));
                await LoadSystemFieldsAsync(DetailHref("self"), DetailHref("versions"), DetailTitle);
            }
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }

    // 2+ server items are selected → the "File multiple items" button is offered (ADR "Bulk-file multiple
    // inbox items"). Set from the list's selection in code-behind.
    [ObservableProperty] private bool _canFileMultiple;

    // Files several server intray items into one folder, best-effort, each with the same optional feed comment.
    public async Task FileMultipleServerItemsAsync(IReadOnlyList<IntrayItemViewModel> items, Guid folderId, string? comment)
    {
        if (_api is null || items.Count == 0)
        {
            return;
        }

        var filed = 0;
        foreach (var item in items)
        {
            try
            {
                await _api.Intray.FileIntrayItemAsync(item.Item!, folderId, comment);
                filed++;
            }
            catch (Exception)
            {
                // Best-effort: skip an item that can't be filed (e.g. a permission error), keep filing the rest.
            }
        }

        Status = string.Format(Strings.Get("StFiledOf"), filed, items.Count);
        await RefreshIntrayAsync();
    }
    // Builds the filing dialog VM, passing the Repositories tab's selected document (if any) so the dialog can
    // offer filing as a new version of it / into its folder (ADR "Context-aware inbox filing dialog").
    public FolderPickerViewModel CreateFolderPickerViewModel()
    {
        if (_api is null)
        {
            return null!;
        }

        DocumentFilingContext? context = null;
        if (SelectedItem is { IsFolder: false, IsReference: false, IsArchiveEntry: false, IsArchiveBack: false } doc
            && _currentFolderId is { } folderId)
        {
            var folderPath = string.Join(" / ", Breadcrumbs.Select(b => b.Name));
            var folderName = Breadcrumbs.LastOrDefault()?.Name ?? "";
            context = new DocumentFilingContext(doc.Id, doc.Name, $"{folderPath} / {doc.Name}", folderId, folderName, folderPath);
        }

        return new FolderPickerViewModel(_api, context);
    }

    // The bulk filing dialog VM (ADR "Bulk-file multiple inbox items"): only file-in-folder / pick (no
    // as-version). The "in folder" target is the selected repo folder itself, or the selected document's folder.
    public FolderPickerViewModel CreateBulkFolderPickerViewModel()
    {
        if (_api is null)
        {
            return null!;
        }

        DocumentFilingContext? context = null;
        if (SelectedItem is { IsReference: false, IsArchiveEntry: false, IsArchiveBack: false } item)
        {
            var folderPath = string.Join(" / ", Breadcrumbs.Select(b => b.Name));
            if (item.IsFolder)
            {
                context = new DocumentFilingContext(Guid.Empty, "", "", item.Id, item.Name, $"{folderPath} / {item.Name}");
            }
            else if (_currentFolderId is { } folderId)
            {
                context = new DocumentFilingContext(Guid.Empty, "", "", folderId, Breadcrumbs.LastOrDefault()?.Name ?? "", folderPath);
            }
        }

        return new FolderPickerViewModel(_api, context, bulk: true);
    }

    // ---- Search (metadata, ADR "Metadata search (first slice)") ---------------------------------------

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _searchStatus = "";
    [ObservableProperty] private SearchResultViewModel? _selectedSearchResult;

    // ---- Refinement panel (ADR "Search-refinement UI", phase 2) ---------------------------------------

    public sealed record SearchRepoOption(Guid? Id, string Name);

    [ObservableProperty] private bool _filtersExpanded;
    private bool _searchMetadataLoaded;

    public ObservableCollection<SearchRepoOption> SearchRepositories { get; } = [];
    public ObservableCollection<FieldFilterRowViewModel> FieldFilters { get; } = [];

    [ObservableProperty] private SearchRepoOption? _selectedSearchRepository;
    [ObservableProperty] private DateTimeOffset? _docDateFrom;
    [ObservableProperty] private DateTimeOffset? _docDateTo;
    [ObservableProperty] private DateTimeOffset? _createdFrom;
    [ObservableProperty] private DateTimeOffset? _createdTo;
    [ObservableProperty] private string _createdByFilter = "";

    private IReadOnlyList<string> _availableFieldNames = [];
    private IReadOnlyDictionary<string, int> _fieldTypes = new Dictionary<string, int>();

    public bool CanAddFieldFilter => _availableFieldNames.Count > 0;

    // Tab order: 0 Repositories · 1 Intray · 2 Check-out · 3 Search · 4 Recycle bin · 5 Tasks · 6 Users/Groups
    // · 7 Audit · 8 Legal holds · 9 Retention · 10 Tenant · 11 My work · 12 Tag catalog · 13 Contacts
    // · 14 Calendar. Each new tab is APPENDED so the indices above stay put — they are also written out in
    // Program.cs and the tour deep-links, and renumbering them silently opens the wrong tab.
    //
    // A switch expression rather than the if-chain this grew into: fourteen `if (value == n)` blocks read as
    // fourteen independent decisions when they are one, and the chain is what made every added tab cost this
    // file another dozen lines (#517). Each arm hands back the Task the activation needs; anything with more
    // than one statement gets a local function rather than being flattened into the arm.
    async partial void OnSelectedTabChanged(int value)
    {
        Preview.ExitFullscreen(); // leave full screen when switching tabs (the tab strip stays reachable while maximized)
        IntrayPreview.ExitFullscreen();
        RecycleBin.Preview.ExitFullscreen();

        await (value switch
        {
            0 => RefreshRepositoriesViewAsync(),
            1 => RefreshIntrayAsync(),
            2 => ActivateCheckoutAsync(),
            3 => ActivateSearchAsync(),
            4 => LoadRecycleBinAsync(),
            5 => LoadTasksAsync(),
            6 => LoadPrincipalsAsync(),
            7 => ActivateAuditAsync(),
            8 => LoadLegalHoldsAsync(),
            9 => LoadRetentionScheduleAsync(),
            10 => LoadTenantSettingsAsync(),
            11 => LoadMyWorkAsync(),
            12 => LoadTagCatalogAsync(),
            // Contacts and Calendar load on ACTIVATION, not at login: each costs a request per collection and
            // most sessions never open either tab.
            13 => ContactsTab.LoadAsync(),
            14 => CalendarTab.LoadAsync(),
            _ => Task.CompletedTask,
        });
    }

    private async Task ActivateCheckoutAsync()
    {
        await Checkout.LoadAsync();
        OnPropertyChanged(nameof(CheckoutCount));
        OnPropertyChanged(nameof(HasCheckouts));
    }

    private async Task ActivateSearchAsync()
    {
        if (!_searchMetadataLoaded)
        {
            await LoadSearchMetadataAsync();
        }

        await LoadSavedSearchesAsync();
    }

    private async Task ActivateAuditAsync()
    {
        await LoadRetentionAsync();
        if (AuditEvents.Count == 0)
        {
            await LoadAuditPageAsync(reset: true);
        }
    }

    // Returning to the Repositories tab reloads the open folder's contents — so a document filed or re-versioned
    // from another tab (e.g. the Intray) appears — while keeping focus on the selected document (re-selected by
    // id, which re-renders its detail + preview so a new version shows). See ADR "Desktop recycle bin parity".
    private async Task RefreshRepositoriesViewAsync()
    {
        if (_api is null || _currentFolderId is not { } folderId || _archiveDocumentId is not null)
        {
            return;
        }

        var selectedId = SelectedItem?.Id;
        await LoadFolderContentsAsync(folderId);
        if (selectedId is { } id && Items.FirstOrDefault(n => n.Id == id) is { } fresh)
        {
            SelectedItem = fresh;
        }
    }

    // Entering the Recycle bin tab loads its tenant-wide list (ADR "Desktop recycle bin parity").
    private async Task LoadRecycleBinAsync() => await RecycleBin.LoadAsync();

    private async Task LoadSearchMetadataAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var fields = await _api.Search.GetSearchFieldsAsync();
            _availableFieldNames = fields.Select(f => f.Name).ToList();
            _fieldTypes = fields.ToDictionary(f => f.Name, f => f.DataType);
            OnPropertyChanged(nameof(CanAddFieldFilter));

            SearchRepositories.Clear();
            SearchRepositories.Add(new SearchRepoOption(null, "All repositories"));
            foreach (var repo in await _api.Documents.GetRepositoriesAsync())
            {
                SearchRepositories.Add(new SearchRepoOption(repo.Id, repo.Name));
            }

            SelectedSearchRepository = SearchRepositories[0];
            _searchMetadataLoaded = true;
        }
        catch (Exception)
        {
            // Non-fatal — free-text/system filters still work without the field picker.
        }
    }

    [RelayCommand]
    private void AddFieldFilter()
    {
        if (_availableFieldNames.Count > 0)
        {
            FieldFilters.Add(new FieldFilterRowViewModel(_availableFieldNames, _fieldTypes));
        }
    }

    [RelayCommand]
    private void RemoveFieldFilter(FieldFilterRowViewModel row) => FieldFilters.Remove(row);


    // Everything the user entered: text, refinement filters and facet drill-downs, then re-run.
    //
    // The old "Clear filters" reset the panel and left the FACETS applied, so results stayed narrowed by
    // drill-downs that were no longer visible anywhere in the form — there was no way back to an unfiltered
    // search short of restarting (#462). Re-running matters too: an emptied form over stale results reads as
    // "reset did nothing".
    [RelayCommand]
    private async Task ResetSearchCriteriaAsync()
    {
        SearchQuery = "";
        SelectedSearchRepository = SearchRepositories.Count > 0 ? SearchRepositories[0] : null;
        DocDateFrom = DocDateTo = CreatedFrom = CreatedTo = null;
        CreatedByFilter = "";
        FieldFilters.Clear();
        ClearFacetSelections();
        SelectedSearchResult = null;
        SearchPreview.Reset("");
        await SearchAsync();
    }

    private static void AddDateParam(List<string> parameters, string field, string op, DateTimeOffset? value)
    {
        if (value is { } date)
        {
            parameters.Add($"system[{field}][{op}]={date:yyyy-MM-dd}");
        }
    }

    // Runs a search assembled from the free text + repository scope + system/index-field filters.
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_api is null)
        {
            return;
        }

        var parameters = new List<string>();

        var query = SearchQuery.Trim();
        if (query.Length > 0)
        {
            parameters.Add($"q={Uri.EscapeDataString(query)}");
        }

        if (SelectedSearchRepository?.Id is { } repositoryId)
        {
            parameters.Add($"repositoryId={repositoryId}");
        }

        AddDateParam(parameters, "documentDate", "gte", DocDateFrom);
        AddDateParam(parameters, "documentDate", "lte", DocDateTo);
        AddDateParam(parameters, "createdAt", "gte", CreatedFrom);
        AddDateParam(parameters, "createdAt", "lte", CreatedTo);
        if (!string.IsNullOrWhiteSpace(CreatedByFilter))
        {
            parameters.Add($"system[createdBy][contains]={Uri.EscapeDataString(CreatedByFilter.Trim())}");
        }

        foreach (var row in FieldFilters)
        {
            var value = row.WireValue;
            if (string.IsNullOrEmpty(row.FieldName) || string.IsNullOrEmpty(value) || row.SelectedOperator is null)
            {
                continue;
            }

            parameters.Add($"fields[{Uri.EscapeDataString(row.FieldName)}][{row.SelectedOperator.Value}]={Uri.EscapeDataString(value)}");
        }

        // Active facet drill-downs (ADR "Search facet refinements") — each dimension's set becomes an `in` filter
        // (OR within the dimension); the server keeps each dimension open (post-filter faceting).
        AddFacetParam(parameters, "system[documentType][in]", _facetTypeSet);
        AddFacetParam(parameters, "system[fileType][in]", _facetFileTypeSet);
        AddFacetParam(parameters, "system[createdBy][in]", _facetCreatedBySet);
        AddFacetParam(parameters, "system[documentYear][in]", _facetYearSet);
        AddFacetParam(parameters, "system[tag][in]", _facetTagSet);
        AddFacetParam(parameters, "system[sensitivityLabel][in]", _facetSensitivitySet);
        foreach (var (field, set) in _facetFieldSets)
        {
            AddFacetParam(parameters, $"fields[{Uri.EscapeDataString(field)}][in]", set);
        }

        if (parameters.Count == 0)
        {
            SearchResults.Clear();
            FacetTypes.Clear();
            FacetFileTypes.Clear();
            FacetCreatedBy.Clear();
            FacetYears.Clear();
            FacetTags.Clear();
            FacetSensitivity.Clear();
            FieldFacets.Clear();
            HasFacetTypes = HasFacetFileTypes = HasFacetCreatedBy = HasFacetYears = HasFacetTags = HasFacetSensitivity = false;
            SearchStatus = "";
            return;
        }

        LastSearchQueryString = string.Join("&", parameters); // for "Save search" (ADR "Saved searches")
        await ExecuteSearchAsync(LastSearchQueryString);
    }

    // Appends `key=v1,v2` (comma-joined, escaped) when the facet set is non-empty (ADR "Search facet refinements").
    private static void AddFacetParam(List<string> parameters, string key, HashSet<string> values)
    {
        if (values.Count > 0)
        {
            parameters.Add($"{key}={string.Join(",", values.Select(Uri.EscapeDataString))}");
        }
    }

    private void ClearFacetSelections()
    {
        _facetTypeSet.Clear();
        _facetFileTypeSet.Clear();
        _facetCreatedBySet.Clear();
        _facetYearSet.Clear();
        _facetTagSet.Clear();
        _facetSensitivitySet.Clear();
        _facetFieldSets.Clear();
    }

    // Runs a pre-assembled query-params string (shared by the refinement search + a restored saved search).
    private async Task ExecuteSearchAsync(string queryParams)
    {
        if (_api is null)
        {
            return;
        }

        SearchStatus = Strings.Get("StSearching");
        try
        {
            var page = await _api.Search.SearchWithFacetsAsync(queryParams);
            SearchResults.Clear();
            foreach (var result in page.Results)
            {
                SearchResults.Add(new SearchResultViewModel
                {
                    Id = result.Id,
                    Name = result.Name,
                    IsFolder = result.IsFolder,
                    ParentId = result.ParentId,
                    Path = result.Path,
                    Highlight = result.Highlight,
                    VersionsHref = result.VersionsHref,
                    Links = result.Links,
                });
            }

            PopulateFacets(FacetTypes, page.Facets.DocumentTypes, _facetTypeSet);
            PopulateFacets(FacetFileTypes, page.Facets.FileTypes, _facetFileTypeSet);
            PopulateFacets(FacetCreatedBy, page.Facets.CreatedBy, _facetCreatedBySet);
            PopulateFacets(FacetYears, page.Facets.Years, _facetYearSet);
            PopulateFacets(FacetTags, page.Facets.Tags, _facetTagSet);
            PopulateFacets(FacetSensitivity, page.Facets.SensitivityLabels, _facetSensitivitySet);
            HasFacetTypes = FacetTypes.Count > 0;
            HasFacetFileTypes = FacetFileTypes.Count > 0;
            HasFacetCreatedBy = FacetCreatedBy.Count > 0;
            HasFacetYears = FacetYears.Count > 0;
            HasFacetTags = FacetTags.Count > 0;
            HasFacetSensitivity = FacetSensitivity.Count > 0;

            // Per-Select-field facets (ADR "Search facet refinements") — one group per field, multi-select OR.
            FieldFacets.Clear();
            foreach (var field in page.Facets.Fields)
            {
                var set = _facetFieldSets.TryGetValue(field.Name, out var s) ? s : [];
                var buckets = field.Buckets.Select(b => new FacetBucketViewModel(b.Value, b.Count, set.Contains(b.Value)));
                FieldFacets.Add(new FieldFacetGroupViewModel(field.Name, buckets, ToggleFieldFacet));
            }

            SearchStatus = SearchResults.Count == 0 ? "No matches." : $"{SearchResults.Count} result(s).";
        }
        catch (Exception e)
        {
            SearchStatus = string.Format(Strings.Get("StErrSearch"), e.Message);
        }
    }

    // ---- Saved searches (ADR "Saved searches") ------------------------------------------------------
    public ObservableCollection<SearchClient.SavedSearchInfo> SavedSearches { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSaveSearch))] private string _lastSearchQueryString = "";
    public bool CanSaveSearch => !string.IsNullOrEmpty(LastSearchQueryString);

    // The view provides the "name this search" prompt (a native dialog can't be built in the VM).
    public Func<Task<string?>>? SaveSearchNamePrompt { get; set; }

    public async Task LoadSavedSearchesAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            SavedSearches.Clear();
            foreach (var s in await _api.Search.GetSavedSearchesAsync())
            {
                SavedSearches.Add(s);
            }
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    [RelayCommand]
    private async Task SaveCurrentSearch()
    {
        if (_api is null || !CanSaveSearch || SaveSearchNamePrompt is null)
        {
            return;
        }

        if (await SaveSearchNamePrompt() is not { } name || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await _api.Search.SaveSearchAsync(name.Trim(), LastSearchQueryString);
            Status = Strings.Get("StSearchSaved");
            await LoadSavedSearchesAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task RunSavedSearch(SearchClient.SavedSearchInfo? s)
    {
        if (s is null)
        {
            return;
        }

        LastSearchQueryString = s.QueryString;
        SearchQuery = ExtractQ(s.QueryString);
        ClearFacetSelections();
        await ExecuteSearchAsync(s.QueryString);
    }

    [RelayCommand]
    private async Task DeleteSavedSearch(SearchClient.SavedSearchInfo? s)
    {
        if (_api is null || s is null)
        {
            return;
        }

        try { await _api.Search.DeleteSavedSearchAsync(s); } catch (Exception) { }
        await LoadSavedSearchesAsync();
    }

    // Set to a dialog runner (code-behind) that shows the share dialog for the VM and returns true on Save.
    public Func<ShareSavedSearchViewModel, Task<bool>>? ShowShareSavedSearchDialog { get; set; }

    // Open the scope dialog for my own saved search (ADR "Scoped saved-search sharing") — loads the picker
    // targets + current grants, then owner-only PUTs the chosen scope + principals.
    [RelayCommand]
    private async Task ShareSavedSearch(SearchClient.SavedSearchInfo? s)
    {
        if (_api is null || s is null || !s.IsMine || ShowShareSavedSearchDialog is null)
        {
            return;
        }

        try
        {
            var targets = await _api.Search.GetShareTargetsAsync();
            var current = s.ShareScope == 2
                ? (await _api.Search.GetSavedSearchSharesAsync(s)).Select(g => $"{g.PrincipalType}:{g.PrincipalId}").ToHashSet()
                : [];
            var options = targets.Select(t => new ShareSavedSearchViewModel.PrincipalOption(
                t.Type, t.Id, t.Type == "group" ? $"{t.Name} (group)" : t.Name, current.Contains($"{t.Type}:{t.Id}")));

            var dialogVm = new ShareSavedSearchViewModel(s.Name, s.ShareScope, options);
            if (!await ShowShareSavedSearchDialog(dialogVm))
            {
                return;
            }

            await _api.Search.SetSavedSearchShareAsync(s, dialogVm.Scope, dialogVm.SelectedPrincipals);
            Status = dialogVm.Scope switch { 1 => $"Shared '{s.Name}' with everyone.", 2 => $"Shared '{s.Name}' with specific people.", _ => $"'{s.Name}' is now private." };
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrSharing"), e.Message);
        }

        await LoadSavedSearchesAsync();
    }

    private static string ExtractQ(string queryString)
    {
        foreach (var part in queryString.Split('&'))
        {
            if (part.StartsWith("q=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part[2..]);
            }
        }

        return "";
    }

    // ---- Search facets (ADR "Search facets" / multi-select "Search facet refinements") ---------------
    public ObservableCollection<FacetBucketViewModel> FacetTypes { get; } = [];
    public ObservableCollection<FacetBucketViewModel> FacetFileTypes { get; } = [];
    public ObservableCollection<FacetBucketViewModel> FacetCreatedBy { get; } = [];
    public ObservableCollection<FacetBucketViewModel> FacetYears { get; } = [];
    public ObservableCollection<FacetBucketViewModel> FacetTags { get; } = [];
    public ObservableCollection<FacetBucketViewModel> FacetSensitivity { get; } = [];
    public ObservableCollection<FieldFacetGroupViewModel> FieldFacets { get; } = [];

    // Multi-select facet selections (ADR "Search facet refinements") — a set of chosen values per dimension
    // (OR within the dimension); a per-field dictionary keys the Select index-field facets by name.
    private readonly HashSet<string> _facetTypeSet = [];
    private readonly HashSet<string> _facetFileTypeSet = [];
    private readonly HashSet<string> _facetCreatedBySet = [];
    private readonly HashSet<string> _facetYearSet = [];
    private readonly HashSet<string> _facetTagSet = [];
    private readonly HashSet<string> _facetSensitivitySet = [];
    private readonly Dictionary<string, HashSet<string>> _facetFieldSets = [];

    [ObservableProperty] private bool _hasFacetTypes;
    [ObservableProperty] private bool _hasFacetFileTypes;
    [ObservableProperty] private bool _hasFacetCreatedBy;
    [ObservableProperty] private bool _hasFacetYears;
    [ObservableProperty] private bool _hasFacetTags;
    [ObservableProperty] private bool _hasFacetSensitivity;

    private static void PopulateFacets(ObservableCollection<FacetBucketViewModel> target, IReadOnlyList<SearchClient.SearchFacetBucket> buckets, HashSet<string> selected)
    {
        target.Clear();
        foreach (var b in buckets)
        {
            target.Add(new FacetBucketViewModel(b.Value, b.Count, selected.Contains(b.Value)));
        }
    }

    private Task ToggleFacet(HashSet<string> set, FacetBucketViewModel? b)
    {
        if (b is null)
        {
            return Task.CompletedTask;
        }

        if (!set.Remove(b.Value))
        {
            set.Add(b.Value);
        }

        return SearchAsync();
    }

    [RelayCommand] private Task ToggleFacetType(FacetBucketViewModel? b) => ToggleFacet(_facetTypeSet, b);
    [RelayCommand] private Task ToggleFacetFileType(FacetBucketViewModel? b) => ToggleFacet(_facetFileTypeSet, b);
    [RelayCommand] private Task ToggleFacetCreatedBy(FacetBucketViewModel? b) => ToggleFacet(_facetCreatedBySet, b);
    [RelayCommand] private Task ToggleFacetYear(FacetBucketViewModel? b) => ToggleFacet(_facetYearSet, b);
    [RelayCommand] private Task ToggleFacetTag(FacetBucketViewModel? b) => ToggleFacet(_facetTagSet, b);
    [RelayCommand] private Task ToggleFacetSensitivity(FacetBucketViewModel? b) => ToggleFacet(_facetSensitivitySet, b);

    private Task ToggleFieldFacet(string field, FacetBucketViewModel? b)
    {
        if (!_facetFieldSets.TryGetValue(field, out var set))
        {
            set = _facetFieldSets[field] = [];
        }

        return ToggleFacet(set, b);
    }

    // ---- Tag chip editor (ADR "Document tags") ------------------------------------------------------
    [RelayCommand]
    private void AddTag()
    {
        var t = NewTag.Trim().ToLowerInvariant();
        if (t.Length is > 0 and <= 100 && !EditTags.Contains(t))
        {
            EditTags.Add(t);
        }

        NewTag = "";
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (tag is not null)
        {
            EditTags.Remove(tag);
        }
    }

    // A read-only tag chip click → a filter-only search by that tag on the Search tab.
    [RelayCommand]
    private async Task SearchByTag(string? tag)
    {
        if (tag is null)
        {
            return;
        }

        SelectedTab = 3; // Search
        SearchQuery = "";
        ClearFacetSelections();
        _facetTagSet.Add(tag);
        await SearchAsync();
    }

    // Opens a search result: switch to the Repositories tab and navigate to it (a folder opens itself; a
    // document opens its home folder and selects it).
    // Selecting a result previews it in the Search tab's own pane (#462), with the search terms seeded so the
    // hit overlay marks WHY it matched — the whole reason a preview here is worth more than a file viewer.
    //
    // Generated by [ObservableProperty] on _selectedSearchResult; before #462 the selection had no observer at
    // all and existed only for the double-click handler to read.
    partial void OnSelectedSearchResultChanged(SearchResultViewModel? value) => Safe.Fire(async () =>
    {
        OnPropertyChanged(nameof(HasSelectedSearchResult)); // greys the toolbar's Go to (#530 tranche 8)
        if (_api is null || value is null || value.IsFolder || value.VersionsHref is not { } versionsHref)
        {
            // A folder has nothing to preview, and clearing beats leaving the previous document's page sitting
            // under a folder's name.
            SearchPreview.Reset("");
            return;
        }

        SearchPreview.Reset(Strings.Get("StLoading"));
        SearchPreview.FindQuery = SearchQuery.Trim();
        try
        {
            // Follows the address the ROW advertised rather than turning the id back into one (ADR 0557).
            await SearchPreview.RenderAsync(await _api.Versions.GetPreviewFromVersionsAsync(versionsHref));
        }
        catch (Exception)
        {
            // The preview is an extra, never the tab: an unreachable rendition still leaves a usable result
            // list, and the user can still open the document.
            SearchPreview.Reset("");
        }
    });

    // Leaving the tab for the document itself — from the row's button or a double-click (#462).
    [RelayCommand]
    private async Task OpenSearchResult(SearchResultViewModel result) => await OpenSearchResultAsync(result);

    public async Task OpenSearchResultAsync(SearchResultViewModel result)
    {
        SelectedTab = 0;

        // Carry the search terms into the viewer so the hits highlight on the opened document (ADR "Search hit
        // overlay"). Only for a document result; a folder has no preview.
        if (!result.IsFolder)
        {
            Preview.FindQuery = SearchQuery.Trim();
        }

        if (!result.IsFolder && result.Links?.GetValueOrDefault("parent") is { } parentHref
            && result.Links?.GetValueOrDefault("self") is { } docHref)
        {
            // Reveal the document in context: expand + select its parent folder in the tree, load the folder into
            // the list pane, and select the document there (issue #340). Both loads follow the hit's own
            // advertised addresses (#443); the tree expansion stays id-matching against rows already loaded.
            await RevealDocumentInTreeAsync(result.Id, docHref, parentHref);
        }
        else
        {
            // A folder, or a document filed at a repository root (itself a top-level tree node).
            await RevealFolderInTreeAsync(result.Links?.GetValueOrDefault("self")
                ?? throw new InvalidOperationException($"The search hit '{result.Name}' advertised no 'self' rel (ADR 0543)."));
        }
    }

    // Expands the tree along an ordered ancestor id chain (repository-root first), returning the last node — or null
    // if a link in the chain isn't in the visible tree (e.g. a reference-only path the tree doesn't mirror). The
    // repository roots are top-level Tree nodes carrying their real ids, and real subfolders nest by real id, so the
    // synthetic grouping nodes (Personal launchers / Administration, all Guid.Empty) never match a real ancestor.
    private async Task<TreeNodeViewModel?> ExpandTreePathAsync(IReadOnlyList<Guid> chain)
    {
        IReadOnlyList<TreeNodeViewModel> level = Tree;
        TreeNodeViewModel? node = null;
        foreach (var id in chain)
        {
            node = level.FirstOrDefault(n => n.Id == id);
            if (node is null)
            {
                return null;
            }

            await node.EnsureExpandedAsync();
            level = node.Children;
        }

        return node;
    }

    // Reveal a folder (or a root-level item): expand its ancestors, then select it in the tree so its contents load.
    // ONE fetch of the advertised address serves the id, the ancestors rel and the fallback open (ADR 0557).
    private async Task RevealFolderInTreeAsync(string folderSelfHref)
    {
        if (_api is null)
        {
            return;
        }

        var stub = await _api.GetDocumentByAddressAsync(folderSelfHref);
        var chain = await _api.Documents.GetAncestorsAsync(stub.Links["ancestors"]);
        chain.Add(stub.Id); // ancestors are up to the parent; append the folder itself as the reveal target
        var node = await ExpandTreePathAsync(chain);
        if (node is not null)
        {
            SelectedTreeNode = node; // OnSelectedTreeNodeChanged loads the folder's contents
        }
        else
        {
            // Not mirrored in the tree — fall back to a contents-only open of the already-fetched resource.
            await OpenLoadedFolderAsync(stub.Id, stub.Name, stub.Links, null);
        }
    }

    // Reveal a document: expand + select its parent folder in the tree, load the folder into the list, select the doc.
    private async Task RevealDocumentInTreeAsync(Guid documentId, string documentSelfHref, string parentHref)
    {
        if (_api is null)
        {
            return;
        }

        var node = await ExpandTreePathAsync(
            await _api.Documents.GetAncestorsAsync(await _api.Documents.RelViaSelfAsync(documentSelfHref, "ancestors")));

        // Load the parent folder into the list + select the document (+ its preview) regardless of the tree
        // outcome — following the caller's advertised addresses (#443).
        await OpenFolderAsync(parentHref, documentId);

        // Then reflect it in the tree — select the parent node without re-loading the folder (already loaded above).
        if (node is not null)
        {
            _suppressTreeSelectionLoad = true;
            SelectedTreeNode = node;
            _suppressTreeSelectionLoad = false;
        }
    }

    // Uploads files dropped onto the contents pane as new documents. Dropped onto a folder row, they go into
    // that folder (overrideFolderId); anywhere else, the currently-open folder. See ADR "Desktop drag-and-drop
    // upload" + ADR "List-pane drop filing".
    public async Task UploadDroppedFilesAsync(IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files, IReadOnlyDictionary<string, string>? targetFolderLinks = null)
    {
        // The drop target's own addresses, carried by the row it landed on (ADR 0555), else the open folder's.
        // Resolved ONCE for the whole drop — following a rel must not cost a request per file (ADR 0557).
        var folderLinks = targetFolderLinks ?? _currentFolderLinks;
        if (_api is null || folderLinks is null)
        {
            Status = Strings.Get("StSelectFolderDrop");
            return;
        }

        var childrenHref = folderLinks["children"];

        var uploaded = 0;
        var failed = 0;
        foreach (var file in files)
        {
            // Declared out here so the name-conflict recovery below can re-file the SAME bytes rather than
            // reading the file a second time.
            byte[] bytes = [];
            try
            {
                Status = string.Format(Strings.Get("StUploadingFile"), file.Name);
                await using (var stream = await file.OpenReadAsync())
                using (var buffer = new MemoryStream())
                {
                    await stream.CopyToAsync(buffer);
                    bytes = buffer.ToArray();
                }

                // Duplicate detection (ADR "Duplicate document detection"): if an identical document already exists,
                // offer to reference it / file anyway / cancel before uploading a second copy.
                if (DuplicateUploadDialog is { } prompt)
                {
                    var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                    var dups = await _api.Documents.FindDuplicatesAsync(hash);
                    if (dups.Count > 0)
                    {
                        var choice = await prompt(new DuplicatePromptRequest(file.Name, dups));
                        if (choice is null || choice.Action == "cancel")
                        {
                            continue;
                        }

                        if (choice.Action == "reference")
                        {
                            await _api.Documents.CreateReferenceAsync(folderLinks["references"], choice.TargetId);
                            uploaded++;
                            continue;
                        }
                        // "file" → fall through and upload a second copy.
                    }
                }

                await _api.Documents.UploadFileAsync(childrenHref, file.Name, bytes);
                uploaded++;
            }
            catch (Services.DocumentNameTakenException) when (NameConflictDialog is not null)
            {
                // The name is taken. Reporting that and dropping the file is what made a drag-and-drop appear to
                // do nothing, so ask what was meant instead (a new version, or a new name) and carry it out.
                var resolver = new Services.UploadConflictResolver(_api);
                if (await resolver.ResolveAsync(childrenHref, file.Name, bytes, NameConflictDialog, m => Status = m))
                {
                    uploaded++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Services.ApiActionException e)
            {
                Status = e.Message;
                failed++;
            }
            catch (Exception e)
            {
                Status = string.Format(Strings.Get("StErrUpload2"), file.Name, e.Message);
                failed++;
            }
        }

        if (_currentFolderId is { } openFolderId)
        {
            await LoadFolderContentsAsync(openFolderId);
        }

        Status = string.Format(Strings.Get("StUploadedN"), uploaded) + (failed > 0 ? string.Format(Strings.Get("StFailedN"), failed) : "") + ".";
    }

    // Dropping OS files onto a document row offers the intray-style filing dialog (ADR "List-pane drop filing"):
    // file as a new version of that document, or into its folder, with an optional feed comment. Builds the
    // picker VM (single-file → as-version available; multi-file → bulk, folder-only). The view shows the dialog
    // and calls FileDroppedFilesAsync with the result.
    public FolderPickerViewModel? CreateDropFilingPickerViewModel(NodeViewModel document, int fileCount)
    {
        if (_api is null || document.IsFolder)
        {
            return null;
        }

        var folderId = document.IsReference ? document.RealParentId ?? _currentFolderId : _currentFolderId;
        if (folderId is not { } fid)
        {
            return null;
        }

        var folderPath = string.Join(" / ", Breadcrumbs.Select(b => b.Name));
        var folderName = Breadcrumbs.LastOrDefault()?.Name ?? "";
        var context = new DocumentFilingContext(document.Id, document.Name, $"{folderPath} / {document.Name}", fid, folderName, folderPath,
            document.Links,
            // "Into its folder": a reference row's real home travels as its `go-to` address; a real row files
            // into the OPEN folder, whose links the navigation stored (ADR 0555).
            document.IsReference ? document.Links?.GetValueOrDefault("go-to") is { } goTo ? new Dictionary<string, string> { ["self"] = goTo } : null : _currentFolderLinks);
        return new FolderPickerViewModel(_api, context, bulk: fileCount > 1);
    }

    // Applies a list-pane drop-filing choice to the dropped files (ADR "List-pane drop filing"): file as a new
    // version of the target document, or as new documents in the chosen folder, each carrying the feed comment.
    public async Task FileDroppedFilesAsync(IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files, FilingResult result)
    {
        if (_api is null || files.Count == 0)
        {
            return;
        }

        var done = 0;
        var failed = 0;
        foreach (var file in files)
        {
            try
            {
                Status = string.Format(Strings.Get("StFilingFile"), file.Name);
                byte[] bytes;
                await using (var stream = await file.OpenReadAsync())
                using (var buffer = new MemoryStream())
                {
                    await stream.CopyToAsync(buffer);
                    bytes = buffer.ToArray();
                }

                if (result.Mode == FilingMode.AsVersion)
                {
                    await _api.Documents.UploadNewVersionAsync(await TargetHrefAsync(result, "versions"), bytes, Path.GetExtension(file.Name), result.Comment);
                }
                else
                {
                    await _api.Documents.UploadFileAsync(await TargetHrefAsync(result, "children"), file.Name, bytes, result.Comment);
                }

                done++;
            }
            catch (Services.ApiActionException e)
            {
                Status = e.Message;
                failed++;
            }
            catch (Exception e)
            {
                Status = string.Format(Strings.Get("StErrFiling2"), file.Name, e.Message);
                failed++;
            }
        }

        // Reloading the folder rebuilds Items (clearing the selection), so capture the target first.
        var refreshDetailFor = result.Mode == FilingMode.AsVersion && SelectedItem?.Id == result.TargetId ? result.TargetId : (Guid?)null;
        if (_currentFolderId is { } folderId)
        {
            await LoadFolderContentsAsync(folderId);
        }

        // If we filed a new version of the currently-open document, refresh its detail (new version + comment).
        if (refreshDetailFor is { } targetId && Items.FirstOrDefault(n => n.Id == targetId) is { } node)
        {
            SelectedItem = node;
            await LoadDetailAsync(node);
        }

        Status = result.Mode == FilingMode.AsVersion
            ? $"Filed {done} new version(s)" + (failed > 0 ? $", {failed} failed" : "") + "."
            : $"Filed {done} file(s)" + (failed > 0 ? $", {failed} failed" : "") + ".";
    }

    // The filing target's address for a rel: from the links the picked row carried where it advertised the
    // rel, else resolved once through the row's document address (ADR 0559).
    private async Task<string> TargetHrefAsync(FilingResult result, string rel) =>
        result.TargetLinks?.GetValueOrDefault(rel)
        ?? await _api!.Documents.RelViaSelfAsync(
            result.TargetLinks?.GetValueOrDefault("self")
            ?? throw new InvalidOperationException($"The filing target advertised no address at all (ADR 0543)."),
            rel);

    // Refresh reloads both the folders-only tree (LoadRootAsync/ReloadTreeAsync) and the current folder's
    // contents — the tree caches lazily-loaded children, so without an explicit rebuild it never picks up
    // structural changes.
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_api is null)
        {
            return;
        }

        if (_currentFolderId is { } id)
        {
            await ReloadTreeAsync();
            await LoadFolderContentsAsync(id);
        }
        else
        {
            await LoadRootAsync();
        }
    }

    // ---- Detail (index-data + preview + chat) ---------------------------------------------------------

    // Reloads the selected document's detail (e.g. after a version restore, ADR "Version restore", so the
    // preview + system fields reflect the new current version).
    public async Task ReloadSelectedDetailAsync()
    {
        // Folders included since issue #408: a folder is a Document with a mask, and it now gets the same pane
        // rather than a thinner one of its own. Archive entries stay out — they are zip contents, not documents.
        if (SelectedItem is { IsArchiveEntry: false, IsArchiveBack: false } document)
        {
            await LoadDetailAsync(document);
        }
    }

    private async Task LoadDetailAsync(NodeViewModel document)
    {
        if (_api is null)
        {
            return;
        }

        // The detail pane fits its CONTENT (ADR 0550): its right height is decided by what is selected — a few
        // rows for a folder, many for a long mask — so a height dragged for one document is wrong for the next.
        // A drag overrides it only until the selection changes, which is now; it is never persisted.
        if (!IndexCollapsed)
        {
            IndexHeight = GridLength.Auto;
        }

        _selectedDocumentId = document.Id;
        _detailIsFolder = document.IsFolder;
        // A stand-in node purely to borrow the tree's glyph rules — hasChildren drives the empty-folder variant.
        _detailGlyphNode = document.IsFolder
            ? new TreeNodeViewModel(document.Id, document.Name, false, null, hasChildren: document.HasChildren)
            : null;
        OnPropertyChanged(nameof(DetailIsFolder));
        OnPropertyChanged(nameof(DetailGlyph));
        OnPropertyChanged(nameof(DetailGlyphBrushKey));
        DetailTitle = document.Name;
        IndexFields.Clear();
        Comments.Clear();
        IsEditing = false;
        CanEditDetail = true;
        Preview.Reset("Loading…");

        try
        {
            var mask = await _api.Documents.GetMaskAsync(document.Href("mask"));
            MaskLine = mask.Name is null ? "No mask" : $"Mask: {mask.Name}" + (mask.VersionNumber is { } v ? $" · version {v}" : "");

            foreach (var field in await _api.Documents.GetIndexDataAsync(document.Href("index-data")))
            {
                IndexFields.Add(new IndexFieldViewModel { FieldName = field.FieldName, Values = string.Join(", ", field.Values) });
            }

            await LoadSystemFieldsAsync(document.DocumentSelfHref, document.Href("versions"), document.Name);
            await LoadPreviewAsync(document.Href("versions"));
            await LoadCommentsAsync(document.Href("chat"));
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad2"), document.Name, e.Message);
        }
    }

    // ---- Users & groups administration (ADR "Users & groups administration tab") ---------------------

    // Gates the Users & groups tab (set from whoami on login); true for a tenant admin / CanManageUsers holder.
    [ObservableProperty] private bool _canManageUsers;
    [ObservableProperty] private bool _canManageServiceAccounts;

    public ObservableCollection<PrincipalRowViewModel> Principals { get; } = [];
    public ObservableCollection<PrincipalRightViewModel> PrincipalRights { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPrincipal))]
    private PrincipalRowViewModel? _selectedPrincipal;

    public bool HasSelectedPrincipal => SelectedPrincipal is not null;

    [ObservableProperty] private string _principalRightsHeader = "";
    [ObservableProperty] private bool _selectedPrincipalIsGroup;
    [ObservableProperty] private bool _ugBusy;

    // The rights matrix is read-only until Edit is clicked; then Save/Cancel show (Cancel reverts). Mirrors the
    // Repositories detail pane's single-edit toggle and the web tab (ADR "Desktop recycle bin parity").
    [ObservableProperty] private bool _ugEditingRights;

    [RelayCommand]
    private void BeginRightsEdit() => UgEditingRights = true;

    [RelayCommand]
    private void CancelRightsEdit()
    {
        UgEditingRights = false;
        RebuildRightsMatrix(SelectedPrincipal); // discard unsaved checkbox changes
        if (SelectedPrincipal is { } p) SelectedPrincipalClearance = p.Rights.ClearanceRank;
    }

    private void RebuildRightsMatrix(PrincipalRowViewModel? value)
    {
        PrincipalRights.Clear();
        if (value is null)
        {
            return;
        }

        for (var i = 0; i < RightLabels.Length; i++)
        {
            PrincipalRights.Add(new PrincipalRightViewModel(RightLabels[i], RightAt(value.Rights, i)));
        }
    }

    // The rights matrix labels, in SystemRightsData constructor order (so the checkbox states rebuild it 1:1).
    private static readonly string[] RightLabels =
    [
        "Tenant administrator", "Impersonate", "Override checkout", "Legal hold",
        "Manage classification", "Reset MFA", "Manage repositories", "Manage masks",
        "Manage service accounts", "Manage users & groups", "View audit log", "Export", "Import",
        "Manage intrayes", "Create external links",
    ];

    async partial void OnSelectedPrincipalChanged(PrincipalRowViewModel? value)
    {
        UgEditingRights = false; // selecting a principal exits edit mode
        PrincipalRights.Clear();
        if (value is null)
        {
            PrincipalRightsHeader = "";
            return;
        }

        SelectedPrincipalIsGroup = value.IsGroup;
        SelectedPrincipalIsUser = !value.IsGroup;
        OnPropertyChanged(nameof(SelectedPrincipalMfaStatus));
        OnPropertyChanged(nameof(CanResetSelectedPrincipalMfa));
        OnPropertyChanged(nameof(CanImpersonateSelectedPrincipal));
        PrincipalRightsHeader = $"{value.Name} — {(value.IsGroup ? "group" : "user")} rights";
        RebuildRightsMatrix(value);
        SelectedPrincipalClearance = value.Rights.ClearanceRank;

        await LoadSelectedPrincipalPhotoAsync(value);
        await LoadGroupMembersAsync(value);
    }

    // ---- Group membership (ADR "Group membership editing") ------------------------------------------

    public ObservableCollection<UserOptionInfo> GroupMembers { get; } = [];
    public ObservableCollection<UserOptionInfo> MemberCandidates { get; } = [];

    [ObservableProperty] private bool _hasGroupMembers;

    // The AutoCompleteBox's selected candidate — setting it (a pick) adds that user, then resets.
    [ObservableProperty] private UserOptionInfo? _selectedMemberToAdd;

    async partial void OnSelectedMemberToAddChanged(UserOptionInfo? value)
    {
        if (value is null || _api is null || SelectedPrincipal is not { IsGroup: true } group)
        {
            return;
        }

        try
        {
            await _api.Admin.AddGroupMemberAsync(group.Source!, value.Id);
            await LoadGroupMembersAsync(group);
            Status = string.Format(Strings.Get("StAdded"), value.DisplayName);
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrAddMember");
        }

        SelectedMemberToAdd = null; // reset the picker for the next add
    }

    private async Task LoadGroupMembersAsync(PrincipalRowViewModel? p)
    {
        GroupMembers.Clear();
        MemberCandidates.Clear();
        HasGroupMembers = false;
        if (_api is null || p is null || !p.IsGroup)
        {
            return;
        }

        try
        {
            foreach (var m in await _api.Admin.GetGroupMembersAsync(p.Source!))
            {
                GroupMembers.Add(m);
            }

            HasGroupMembers = GroupMembers.Count > 0;
            RebuildMemberCandidates();
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadMembers");
        }
    }

    // Non-member tenant users, for the add-picker.
    private void RebuildMemberCandidates()
    {
        MemberCandidates.Clear();
        var memberIds = GroupMembers.Select(m => m.Id).ToHashSet();
        foreach (var p in Principals.Where(p => !p.IsGroup && !memberIds.Contains(p.Id)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            MemberCandidates.Add(new UserOptionInfo(p.Id, p.Name));
        }
    }

    [RelayCommand]
    private async Task RemoveMember(UserOptionInfo? member)
    {
        if (member is null || _api is null || SelectedPrincipal is not { IsGroup: true } group)
        {
            return;
        }

        try
        {
            await _api.Admin.RemoveGroupMemberAsync(member);
            GroupMembers.Remove(member);
            HasGroupMembers = GroupMembers.Count > 0;
            RebuildMemberCandidates();
            Status = string.Format(Strings.Get("StRemovedName"), member.DisplayName);
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrRemoveMember");
        }
    }

    // ---- Profile photo (ADR "User profile photo") ---------------------------------------------------

    [ObservableProperty] private bool _selectedPrincipalIsUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPrincipalPhoto))]
    private Bitmap? _selectedPrincipalPhoto;

    public bool HasSelectedPrincipalPhoto => SelectedPrincipalPhoto is not null;

    public string SelectedPrincipalInitials => Initials(SelectedPrincipal?.Name);

    // ---- Two-factor (ADR "MFA (interactive login, TOTP)") -------------------------------------------
    // The current user's MFA status (from whoami) gates the account-menu Enable/Disable items; CanResetMfa
    // gates the admin reset on the selected user.
    [ObservableProperty] private bool _mfaEnabled;
    [ObservableProperty] private bool _canResetMfa;

    // Exposed for the MFA setup dialog, which drives enroll/enable interactively against the API.
    public SimplArchiveApiClient? Api => _api;

    public string SelectedPrincipalMfaStatus => SelectedPrincipal is { IsGroup: false } p
        ? $"Two-factor: {(p.MfaEnabled ? "enabled" : "off")}"
        : "";

    public bool CanResetSelectedPrincipalMfa => CanResetMfa && SelectedPrincipal is { IsGroup: false, MfaEnabled: true };

    // Impersonate action shows for a selected active, non-admin user when the caller can impersonate and isn't
    // already impersonating (ADR "User impersonation"). The server enforces the rules regardless.
    public bool CanImpersonateSelectedPrincipal => CanImpersonate && !IsImpersonating
        && SelectedPrincipal is { IsGroup: false, IsActive: true, Rights: { IsTenantAdmin: false, CanImpersonate: false } };

    // Called after the user finishes the enroll dialog — reflects the new state in the account menu.
    public void MarkMfaEnabled() => MfaEnabled = true;

    public async Task DisableMyMfaAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Profile.DisableMfaAsync();
            MfaEnabled = false;
            Status = Strings.Get("StMfaDisabled");
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrDisableMfa");
        }
    }

    public async Task ResetSelectedUserMfaAsync()
    {
        if (_api is null || SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        try
        {
            await _api.Admin.ResetUserMfaAsync(p.Source!);
            p.MfaEnabled = false;
            OnPropertyChanged(nameof(SelectedPrincipalMfaStatus));
            OnPropertyChanged(nameof(CanResetSelectedPrincipalMfa));
            Status = string.Format(Strings.Get("StMfaResetFor"), p.Name);
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrResetMfa");
        }
    }

    // ---- Legal holds (ADR "Legal hold & retention enforcement") -------------------------------------
    // Gates the Legal Holds tab + the place/release actions (set from whoami on login).
    [ObservableProperty] private bool _canLegalHold;

    public ObservableCollection<LegalHoldRowViewModel> LegalHolds { get; } = [];
    public ObservableCollection<LegalHoldItemRowViewModel> SelectedHoldItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHold))]
    [NotifyPropertyChangedFor(nameof(SelectedHoldIsActive))]
    private LegalHoldRowViewModel? _selectedLegalHold;

    public bool HasSelectedHold => SelectedLegalHold is not null;
    public bool SelectedHoldIsActive => SelectedLegalHold is { IsActive: true };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHoldItem))]
    private LegalHoldItemRowViewModel? _selectedHoldItem;

    public bool HasSelectedHoldItem => SelectedHoldItem is not null;

    // Go to the held document in Repositories (review finding) — addressed from the ROW's advertised
    // `document`/`parent` (ADR 0555/0559), never from pane state or a bare id.
    [RelayCommand]
    public async Task GoToHoldItemAsync(LegalHoldItemRowViewModel row)
    {
        SelectedTab = 0;
        var documentHref = row.Item.Href("document")
            ?? throw new InvalidOperationException($"The hold item '{row.DocumentName}' advertised no 'document' rel (ADR 0543/0555).");
        if (row.Item.Href("parent") is { } parentHref)
        {
            await RevealDocumentInTreeAsync(row.DocumentId, documentHref, parentHref);
        }
        else
        {
            // A document filed at a repository root is itself a top-level tree node.
            await RevealFolderInTreeAsync(documentHref);
        }
    }

    [RelayCommand]
    private async Task GoToSelectedHoldItemAsync()
    {
        if (SelectedHoldItem is { } row)
        {
            await GoToHoldItemAsync(row);
        }
    }

    [RelayCommand]
    public async Task LoadLegalHoldsAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var holds = await _api.LegalHolds.GetLegalHoldsAsync();
            var previousId = SelectedLegalHold?.Id;
            LegalHolds.Clear();
            foreach (var h in holds)
            {
                LegalHolds.Add(new LegalHoldRowViewModel(h.Id, h.Name, h.IsActive, h.ItemCount, h));
            }

            SelectedLegalHold = LegalHolds.FirstOrDefault(h => h.Id == previousId);
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadHolds");
        }
    }

    async partial void OnSelectedLegalHoldChanged(LegalHoldRowViewModel? value)
    {
        SelectedHoldItems.Clear();
        SelectedHoldItem = null; // the items are re-fetched — a held-over selection is a stale subject (ADR 0559)
        if (_api is null || value is null)
        {
            return;
        }

        try
        {
            var hold = await _api.LegalHolds.GetLegalHoldAsync(value.Hold);
            foreach (var item in hold.Items)
            {
                SelectedHoldItems.Add(new LegalHoldItemRowViewModel(item.DocumentId, item.DocumentName, item));
            }
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    // Creates a new matter (optionally covering a document) — the (name, reason) come from the dialog.
    public async Task<bool> CreateLegalHoldAsync(string name, string? reason, Guid? documentId)
    {
        if (_api is null)
        {
            return false;
        }

        try
        {
            var hold = await _api.LegalHolds.CreateLegalHoldAsync(name, reason);
            if (documentId is { } docId)
            {
                await _api.LegalHolds.AddLegalHoldItemAsync(hold, docId);
                await ReloadCurrentFolderAsync(); // refresh the lock indicator
            }

            Status = string.Format(Strings.Get("StHoldCreated"), name);
            await LoadLegalHoldsAsync();
            return true;
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
            return false;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrCreateHold");
            return false;
        }
    }

    public async Task ReleaseSelectedHoldAsync()
    {
        if (_api is null || SelectedLegalHold is not { } hold)
        {
            return;
        }

        try
        {
            await _api.LegalHolds.ReleaseLegalHoldAsync(hold.Hold);
            Status = Strings.Get("StHoldReleased");
            await LoadLegalHoldsAsync();
            await ReloadCurrentFolderAsync();
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrReleaseHold");
        }
    }

    [RelayCommand]
    public async Task RemoveHoldItemAsync(LegalHoldItemRowViewModel row)
    {
        if (_api is null || SelectedLegalHold is not { } hold)
        {
            return;
        }

        try
        {
            await _api.LegalHolds.RemoveLegalHoldItemAsync(row.Item);
            var reselect = hold;
            await LoadLegalHoldsAsync();
            SelectedLegalHold = LegalHolds.FirstOrDefault(h => h.Id == reselect.Id);
            OnSelectedLegalHoldChanged(SelectedLegalHold);
            await ReloadCurrentFolderAsync();
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrRemoveFromHold");
        }
    }

    private async Task ReloadCurrentFolderAsync()
    {
        if (_currentFolderId is { } folderId)
        {
            await LoadFolderContentsAsync(folderId);
        }
    }

    // ---- Retention schedule (ADR "Retention policies (auto-disposition)") ---------------------------
    [ObservableProperty] private bool _canManageClassification;

    public ObservableCollection<RetentionRowViewModel> RetentionItems { get; } = [];

    [ObservableProperty] private bool _retentionRequiresReview;

    // The view code-behind provides the "extend retention" date dialog (a native window can't be built here).
    public Func<string, Task<string?>>? ExtendRetentionDialog { get; set; }

    // Set by the view: shows the upload-time duplicate modal (ADR "Duplicate document detection") and returns the
    // user's choice (reference / file / cancel), or null if dismissed.
    public Func<DuplicatePromptRequest, Task<DuplicatePromptResult?>>? DuplicateUploadDialog { get; set; }

    // Set by the view: shows the name-conflict modal when a dropped file's name is already taken in the target
    // folder, and returns what the user meant (a new version / a new name), or null if dismissed. The decision
    // and the filing that follows live in Services.UploadConflictResolver — only the window is the view's job.
    public Func<Services.UploadConflictResolver.NameConflictRequest, Task<Services.UploadConflictResolver.NameConflictChoice?>>? NameConflictDialog { get; set; }

    public sealed record DuplicatePromptRequest(string FileName, IReadOnlyList<DocumentsClient.DuplicateInfo> Duplicates);
    public sealed record DuplicatePromptResult(string Action, Guid TargetId);

    [RelayCommand]
    public async Task LoadRetentionScheduleAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var schedule = await _api.LegalHolds.GetRetentionScheduleAsync();
            RetentionRequiresReview = schedule.RequiresReview;
            RetentionItems.Clear();
            foreach (var item in schedule.Items)
            {
                RetentionItems.Add(new RetentionRowViewModel(item.DocumentId, item.DocumentName, item.RetentionYears, item.DispositionDate, item.Overdue, item.SuspendedByHold, item.RetentionOverrideUntil, item));
            }
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadRetention");
        }
    }

    [RelayCommand]
    private async Task DisposeRetention(RetentionRowViewModel? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            await _api.LegalHolds.DisposeRetentionAsync(row.Item);
            Status = string.Format(Strings.Get("StDisposed"), row.DocumentName);
            await LoadRetentionScheduleAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task ExtendRetention(RetentionRowViewModel? row)
    {
        if (_api is null || row is null || ExtendRetentionDialog is null)
        {
            return;
        }

        if (await ExtendRetentionDialog(row.DocumentName) is not { } until)
        {
            return;
        }

        try
        {
            await _api.LegalHolds.ExtendRetentionAsync(row.Item, until);
            Status = string.Format(Strings.Get("StExtendedRetention"), row.DocumentName);
            await LoadRetentionScheduleAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }

    // ---- Tenant-admin self-service settings (ADR "Tenant-admin settings tab") ----------------------
    // Read-only until Edit; Save/Cancel in edit mode. Gated on IsTenantAdmin (the tab's IsVisible).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasTenantSettings))] private bool _tenantSettingsLoaded;

    [ObservableProperty] private string _tenantName = "";
    [ObservableProperty] private int _tenantAuditRetentionDays;
    [ObservableProperty] private int _tenantCheckoutTtlDays;
    [ObservableProperty] private int _tenantCheckoutWarningDays = 1;
    // The WORM lock mode as a ComboBox SelectedIndex: 0 = Governance, 1 = Compliance.
    [ObservableProperty] private int _tenantWormLockModeIndex;
    [ObservableProperty] private bool _tenantRequireMfa;
    [ObservableProperty] private bool _tenantAllowPasskeyLogin;
    [ObservableProperty] private bool _tenantRequireDispositionReview;
    [ObservableProperty] private bool _tenantRestrictTagsToCatalog;
    // Data-classification clearance enforcement (ADR "Sensitivity clearance enforcement").
    [ObservableProperty] private bool _tenantEnforceClearance;

    // External links (ADR 0546, issue #385). The two caps only mean anything while the switch is on, so the UI
    // reveals them with it — one yes/no decision, then its bounds.
    [ObservableProperty] private bool _tenantAllowExternalLinks;

    // Whether an existing link's URL may be revealed again (issue #412). Threaded through EVERY site below:
    // the tenant-settings PUT is a FULL replacement, so a field missing from the call would silently reset it
    // — which is exactly the bug #404 fixed.
    [ObservableProperty] private bool _tenantShowExternalLinkUrl;
    [ObservableProperty] private int _tenantExternalLinkMaxDays = 180;
    [ObservableProperty] private int _tenantExternalLinkDefaultAccesses = 5;
    // Per-tenant storage quota (ADR "Per-tenant storage quota"): the editable limit in MB (null = unlimited) and a
    // read-only "used of limit" display line.
    [ObservableProperty] private int? _tenantStorageQuotaMb;
    [ObservableProperty] private string _tenantStorageUsage = "";
    [ObservableProperty] private string _tenantStorageWarning = "";
    // Per-tenant bucket lifecycle: abort incomplete multipart uploads after N days (0 = off, ADR "Per-tenant
    // bucket policy knobs").
    [ObservableProperty] private int _tenantIncompleteUploadCleanupDays;
    // Audit webhook / SIEM streaming (ADR "Audit webhook streaming"). The secret is write-only; the box is left
    // blank on load and a non-empty value (re)sets it. TenantWebhookConfigured reports whether one is stored.
    [ObservableProperty] private string _tenantAuditWebhookUrl = "";
    [ObservableProperty] private string _tenantAuditWebhookSecret = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TenantWebhookSecretStatus))]
    [NotifyPropertyChangedFor(nameof(TenantWebhookSecretWatermark))]
    private bool _tenantWebhookConfigured;

    public string TenantWebhookSecretStatus => TenantWebhookConfigured ? "Signing secret: configured" : "Signing secret: not set";
    public string TenantWebhookSecretWatermark => TenantWebhookConfigured ? "Leave blank to keep the current secret" : "Required to enable the webhook";

    // Read-only delivery health (ADR "Audit webhook delivery retry/backoff") shown when a webhook URL is set.
    private static readonly Avalonia.Media.IBrush HealthyBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2e7d32"));
    private static readonly Avalonia.Media.IBrush FailingBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e65100"));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TenantWebhookHealthVisible))]
    [NotifyPropertyChangedFor(nameof(TenantWebhookHealthBrush))]
    private string _tenantWebhookHealth = "";
    public bool TenantWebhookHealthy { get; private set; }
    public bool TenantWebhookHealthVisible => !string.IsNullOrEmpty(TenantWebhookHealth);
    public Avalonia.Media.IBrush TenantWebhookHealthBrush => TenantWebhookHealthy ? HealthyBrush : FailingBrush;
    [ObservableProperty] private string _tenantOcrDisplay = "";
    [ObservableProperty] private string _tenantId = "";
    [ObservableProperty] private string _tenantStatus = "";
    [ObservableProperty] private string _tenantCreated = "";

    public bool HasTenantSettings => TenantSettingsLoaded;

    // The staged, ordered OCR codes while editing (edited via the same ordered picker as the detail pane).
    private List<string> _tenantStagedOcrCodes = [];

    [RelayCommand]
    public async Task LoadTenantSettingsAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var s = await _api.Admin.GetTenantSettingsAsync();
            ApplyTenantSettings(s);
            TenantEditingGroup = null;
            TenantSettingsLoaded = true;
            if (_ocrCatalog.Count == 0)
            {
                try { _ocrCatalog = await _api.GetOcrLanguageCatalogAsync(); }
                catch (Exception) { /* leave empty; the picker just won't offer names */ }
            }
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadTenant");
        }
    }

    private void ApplyTenantSettings(AdminClient.TenantSettingsInfo s)
    {
        LastTenantSettings = s; // group saves follow this resource's settings-<group> rels (ADR 0543)
        TenantName = s.Name;
        TenantAuditRetentionDays = s.AuditRetentionDays;
        TenantCheckoutTtlDays = s.CheckoutTtlDays;
        TenantCheckoutWarningDays = s.CheckoutWarningDays;
        TenantWormLockModeIndex = s.WormLockMode;
        TenantRequireMfa = s.RequireMfa;
        TenantAllowPasskeyLogin = s.AllowPasskeyLogin;
        TenantRequireDispositionReview = s.RequireDispositionReview;
        TenantRestrictTagsToCatalog = s.RestrictTagsToCatalog;
        TenantEnforceClearance = s.EnforceClearance;
        TenantAllowExternalLinks = s.AllowExternalLinks;
        TenantShowExternalLinkUrl = s.ShowExternalLinkUrl;
        TenantExternalLinkMaxDays = s.ExternalLinkMaxDays;
        TenantExternalLinkDefaultAccesses = s.ExternalLinkDefaultAccesses;
        TenantStorageQuotaMb = s.StorageQuotaBytes is { } b ? (int)(b / (1024 * 1024)) : null;
        if (s.StorageQuotaBytes is { } quota && quota > 0)
        {
            // Soft-quota indicator (ADR "Storage soft-quota warnings") — matches the server's 80%/95% thresholds.
            var pct = (int)(s.StorageUsedBytes * 100 / quota);
            TenantStorageUsage = $"Used: {FormatBytes(s.StorageUsedBytes)} of {FormatBytes(quota)} ({pct}%)";
            TenantStorageWarning = pct >= 95 ? "Almost full" : pct >= 80 ? "Approaching quota" : "";
        }
        else
        {
            TenantStorageUsage = "Used: " + FormatBytes(s.StorageUsedBytes) + " (no limit)";
            TenantStorageWarning = "";
        }
        TenantIncompleteUploadCleanupDays = s.IncompleteUploadCleanupDays;
        TenantAuditWebhookUrl = s.AuditWebhookUrl ?? "";
        TenantAuditWebhookSecret = "";
        TenantWebhookConfigured = s.AuditWebhookConfigured;
        TenantWebhookHealthy = s.AuditWebhookConsecutiveFailures == 0;
        TenantWebhookHealth = DescribeWebhookHealth(s);
        _tenantStagedOcrCodes = s.DefaultOcrLanguages.Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
        TenantOcrDisplay = DescribeOcrLanguages(_tenantStagedOcrCodes);
        TenantId = s.Id.ToString();
        TenantStatus = s.Status;
        TenantCreated = s.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    // Bytes → a human "N.N MB" / "N.N GB" for the storage-usage line (ADR "Per-tenant storage quota").
    private static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.##} GB"
        : $"{bytes / (double)(1L << 20):0.##} MB";

    // The read-only webhook-delivery health line (ADR "Audit webhook delivery retry/backoff"); empty when no
    // webhook is configured.
    private static string DescribeWebhookHealth(AdminClient.TenantSettingsInfo s)
    {
        if (string.IsNullOrEmpty(s.AuditWebhookUrl))
        {
            return "";
        }

        static string When(DateTimeOffset? t) => t is { } v ? v.LocalDateTime.ToString("g") : "";

        if (s.AuditWebhookConsecutiveFailures == 0)
        {
            return s.AuditWebhookLastSuccessAt is { } ok
                ? $"Delivery: healthy — last success {When(ok)}"
                : "Delivery: healthy";
        }

        var plural = s.AuditWebhookConsecutiveFailures == 1 ? "failure" : "failures";
        var error = s.AuditWebhookLastError is { Length: > 0 } e ? $" ({e})" : "";
        var next = s.AuditWebhookNextAttemptAt is { } n ? $"; next retry {When(n)}" : "";
        var last = s.AuditWebhookLastSuccessAt is { } ls ? $"; last success {When(ls)}" : "; never delivered";
        return $"Delivery: failing — {s.AuditWebhookConsecutiveFailures} consecutive {plural}{error}{next}{last}";
    }

    [RelayCommand]
    private async Task RecomputeStorage()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            ApplyTenantSettings(await _api.Admin.RecomputeStorageAsync());
            Status = Strings.Get("StStorageRecomputed");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrRecompute");
        }
    }

    [RelayCommand]
    private async Task TestWebhook()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var (success, error) = await _api.Admin.TestAuditWebhookAsync();
            Status = success ? "Test event delivered successfully." : $"Test delivery failed: {error ?? "unknown error"}";
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrTestEvent");
        }
    }

    // The tenant-default OCR ordered picker state + staging (edited via the shared OcrLanguagePickerDialog).
    public (IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> Catalog, IReadOnlyList<string> Selected) TenantOcrPickerState() =>
        (_ocrCatalog, _tenantStagedOcrCodes);

    public void StageTenantOcrLanguages(IReadOnlyList<string> codes)
    {
        _tenantStagedOcrCodes = codes.ToList();
        TenantOcrDisplay = DescribeOcrLanguages(_tenantStagedOcrCodes);
    }

    public async Task CreateRepositoryAsync(string name)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.CreateRepositoryAsync(name.Trim());
            Status = string.Format(Strings.Get("StCreatedRepo"), name.Trim());
            await ReloadTreeAsync();
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
    }

    // The corner: current user's DisplayName + photo (or initials); the email that used to show here is gone.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(UserInitials))] private string _userDisplayName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfilePhoto))]
    private Bitmap? _profilePhoto;

    public bool HasProfilePhoto => ProfilePhoto is not null;

    public string UserInitials => Initials(UserDisplayName);

    private Guid? _currentUserId;

    private static string Initials(string? name) => string.IsNullOrWhiteSpace(name)
        ? "?"
        : string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    private static Bitmap Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    // ---- Passwords (ADR "User password management") — the dialogs live in the view; the VM does the API.

    public async Task ChangeMyPasswordAsync(string current, string newPassword)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Profile.ChangeMyPasswordAsync(current, newPassword);
            Status = Strings.Get("StPwChanged");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrChangePw");
        }
    }

    // Returns the generated password (shown once by the view), or null on failure.
    public async Task<string?> ResetSelectedUserPasswordAsync()
    {
        if (_api is null || SelectedPrincipal is not { IsGroup: false } p)
        {
            return null;
        }

        try
        {
            var password = await _api.Admin.ResetUserPasswordAsync(p.Source!);
            Status = string.Format(Strings.Get("StPwResetFor"), p.Name);
            return password;
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
            return null;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrResetPw");
            return null;
        }
    }

    private async Task LoadMyPhotoAsync()
    {
        ProfilePhoto = null;
        if (_api is null || _currentUserId is not { } id)
        {
            return;
        }

        try
        {
            // My own avatar is a rel on the `me` resource; the id was only ever a way to rebuild the path.
            var bytes = await _api.Profile.GetMyPhotoAsync();
            ProfilePhoto = bytes is null ? null : Decode(bytes);
        }
        catch (Exception)
        {
            ProfilePhoto = null;
        }
    }

    public async Task SetMyPhotoAsync(byte[] png)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Profile.SetMyPhotoAsync(png);
            await LoadMyPhotoAsync();
            Status = Strings.Get("StPhotoUpdated");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrUpdatePhoto");
        }
    }

    private async Task LoadSelectedPrincipalPhotoAsync(PrincipalRowViewModel? p)
    {
        SelectedPrincipalPhoto = null;
        OnPropertyChanged(nameof(SelectedPrincipalInitials));
        if (_api is null || p is null || p.IsGroup)
        {
            return;
        }

        try
        {
            var bytes = await _api.Admin.GetUserPhotoAsync(p.Source!);
            SelectedPrincipalPhoto = bytes is null ? null : Decode(bytes);
        }
        catch (Exception)
        {
            SelectedPrincipalPhoto = null;
        }
    }

    public async Task SetSelectedUserPhotoAsync(byte[] png)
    {
        if (_api is null || SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        try
        {
            await _api.Admin.SetUserPhotoAsync(p.Source!, png);
            await LoadSelectedPrincipalPhotoAsync(p);
            Status = Strings.Get("StPhotoUpdated");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrUpdatePhoto");
        }
    }

    public async Task RemoveSelectedUserPhotoAsync()
    {
        if (_api is null || SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        try
        {
            await _api.Admin.DeleteUserPhotoAsync(p.Source!);
            SelectedPrincipalPhoto = null;
            Status = Strings.Get("StPhotoRemoved");
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrRemovePhoto");
        }
    }

    private static bool RightAt(AdminClient.SystemRightsData r, int i) => i switch
    {
        0 => r.IsTenantAdmin,
        1 => r.CanImpersonate,
        2 => r.CanOverrideCheckout,
        3 => r.CanLegalHold,
        4 => r.CanManageClassification,
        5 => r.CanResetMfa,
        6 => r.CanManageRepositories,
        7 => r.CanManageMasks,
        8 => r.CanManageServiceAccounts,
        9 => r.CanManageUsers,
        10 => r.CanViewAuditLog,
        11 => r.CanExport,
        12 => r.CanImport,
        13 => r.CanManageIntrayes,
        _ => r.CanCreateExternalLink,
    };

    private AdminClient.SystemRightsData CurrentMatrixRights() => new(
        PrincipalRights[0].IsChecked, PrincipalRights[1].IsChecked, PrincipalRights[2].IsChecked,
        PrincipalRights[3].IsChecked, PrincipalRights[4].IsChecked, PrincipalRights[5].IsChecked,
        PrincipalRights[6].IsChecked, PrincipalRights[7].IsChecked, PrincipalRights[8].IsChecked,
        PrincipalRights[9].IsChecked, PrincipalRights[10].IsChecked, PrincipalRights[11].IsChecked,
        PrincipalRights[12].IsChecked, PrincipalRights[13].IsChecked, PrincipalRights[14].IsChecked,
        SelectedPrincipalClearance);

    public async Task LoadPrincipalsAsync()
    {
        if (_api is null)
        {
            return;
        }

        var previousId = SelectedPrincipal?.Id;
        var previousIsGroup = SelectedPrincipal?.IsGroup;
        try
        {
            var groups = await _api.Admin.GetGroupsAsync();
            var users = await _api.Admin.GetUsersAsync();
            Principals.Clear();
            // Groups first (two-person icon), then users, each alphabetical.
            foreach (var p in groups.Concat(users).OrderByDescending(p => p.IsGroup).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                Principals.Add(new PrincipalRowViewModel(p.IsGroup, p.Id, p.Name, p.IsActive, p.Rights, p.MfaEnabled, p));
            }

            SelectedPrincipal = Principals.FirstOrDefault(p => p.Id == previousId && p.IsGroup == previousIsGroup);
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadUsers");
        }
    }

    [RelayCommand]
    private Task RefreshPrincipals() => LoadPrincipalsAsync();

    // Headless-screenshot mock (no Api) — a couple of groups + users with a rights matrix, for --users.
    public void PopulateUsersGroupsDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserEmail = "demo@simplarchive.local";
        UserDisplayName = "Demo Admin";
        CanManageUsers = true;
        Principals.Clear();
        Principals.Add(new PrincipalRowViewModel(true, Guid.NewGuid(), "Administrators", true,
            new AdminClient.SystemRightsData(true, false, false, false, false, false, true, true, true, true, true, true, true)));
        Principals.Add(new PrincipalRowViewModel(true, Guid.NewGuid(), "Editors", true,
            new AdminClient.SystemRightsData(false, false, false, false, false, false, true, false, false, false, false, false, false)));
        Principals.Add(new PrincipalRowViewModel(false, Guid.NewGuid(), "Demo Admin", true,
            new AdminClient.SystemRightsData(true, false, false, false, false, false, true, true, true, true, true, true, true)));
        Principals.Add(new PrincipalRowViewModel(false, Guid.NewGuid(), "Jane Doe", false,
            new AdminClient.SystemRightsData(false, false, false, false, false, false, false, false, false, false, false, false, false)));
        // Select the Administrators group so the rights matrix + Members section show (mock members, no API).
        SelectedPrincipal = Principals[0];
        GroupMembers.Add(new UserOptionInfo(Guid.NewGuid(), "Demo Admin"));
        GroupMembers.Add(new UserOptionInfo(Guid.NewGuid(), "Jane Doe"));
        HasGroupMembers = true;
        MemberCandidates.Add(new UserOptionInfo(Guid.NewGuid(), "Bob Smith"));
    }

    // Mocks the Audit tab for the headless screenshot (ADR "Desktop audit viewer").
    // A fixed reference "now" for the headless --screenshot demo stubs, so timestamps in the audit / tasks /
    // recycle-bin screens don't shift with the wall clock between runs — that made the auto-generated manual's
    // PDF differ on every regeneration (ADR 0510). The web capture freezes its demo clock the same way.
    internal static readonly DateTimeOffset ScreenshotClock = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    internal void PopulateAuditDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserDisplayName = "Demo Admin";
        IsTenantAdmin = true;
        CanViewAuditLog = true;
        AuditRetentionDays = 365;
        AuditVerifyStatus = "Chain intact (128 events)";
        AuditVerifyValid = true;
        AuditVerifyShown = true;
        WormVerifyStatus = "WORM sealed intact (96 events, 3 segments)";
        WormVerifyValid = true;
        WormVerifyShown = true;
        var now = ScreenshotClock;
        AuditEvents.Add(new AuditEventRowViewModel { Timestamp = now.AddMinutes(-2), ActorName = "Demo Admin", ActorType = "User", Action = "Auth.LoggedIn" });
        AuditEvents.Add(new AuditEventRowViewModel { Timestamp = now.AddMinutes(-9), ActorName = "Demo Admin", ActorType = "User", Action = "Document.Deleted", TargetType = "Document", TargetName = "Invoice 2025-001" });
        AuditEvents.Add(new AuditEventRowViewModel { Timestamp = now.AddMinutes(-15), ActorName = "Demo Admin", ActorType = "User", Action = "Acl.Granted", TargetType = "Document", TargetName = "Contracts", Details = "users …: CanSee, CanReadContent" });
        AuditEvents.Add(new AuditEventRowViewModel { Timestamp = now.AddHours(-1), ActorName = "Demo Admin", ActorType = "User", Action = "User.RightsChanged", TargetType = "User", TargetName = "Jane Doe", Details = "Manage repositories" });
    }

    // Mocks the Tenant tab for the headless screenshot (ADR "Tenant-admin settings tab").
    internal void PopulateTenantSettingsDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserDisplayName = "Demo Admin";
        IsTenantAdmin = true;
        TenantName = "Demo Tenant";
        TenantAuditRetentionDays = 365;
        TenantCheckoutTtlDays = 14;
        TenantWormLockModeIndex = 0;
        TenantRequireMfa = true;
        TenantStorageQuotaMb = 250;
        TenantStorageUsage = "Used: 12.4 MB of 250 MB";
        TenantIncompleteUploadCleanupDays = 7;
        _tenantStagedOcrCodes = ["eng", "deu", "fra", "ita"];
        TenantOcrDisplay = "English, German, French, Italian";
        TenantId = Guid.NewGuid().ToString();
        TenantStatus = "Active";
        TenantCreated = ScreenshotClock.AddMonths(-8).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        TenantSettingsLoaded = true;
    }

    [RelayCommand]
    private async Task SaveRights()
    {
        if (_api is null || SelectedPrincipal is not { } p)
        {
            return;
        }

        UgBusy = true;
        try
        {
            var rights = CurrentMatrixRights();
            if (p.IsGroup)
            {
                await _api.Admin.SetRightsAsync(p.Source!, rights);
            }
            else
            {
                await _api.Admin.SetRightsAsync(p.Source!, rights);
            }

            p.Rights = rights;
            UgEditingRights = false;
            Status = Strings.Get("StRightsSaved");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrSaveRights");
        }
        finally
        {
            UgBusy = false;
        }
    }

    // Called from the view's New/Copy code-behind (the create dialog lives in the view). copyRights carries
    // the source principal's rights for Copy; null for a fresh New.
    public async Task CreatePrincipalAsync(bool isGroup, string name, string email, AdminClient.SystemRightsData? copyRights)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var created = isGroup ? await _api.Admin.CreateGroupAsync(name) : await _api.Admin.CreateUserAsync(email, name);
            if (copyRights is not null)
            {
                // The create response IS the resource, rels included, so Copy applies the source's rights by
                // following the new row's own `rights` rel — no re-fetch, no path rebuilt from an id (ADR 0555).
                await _api.Admin.SetRightsAsync(created, copyRights);
            }

            await LoadPrincipalsAsync();
            SelectedPrincipal = Principals.FirstOrDefault(p => p.IsGroup == isGroup && p.Id == created.Id);
            Status = isGroup ? "Group created." : "User created.";
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrCreate");
        }
    }

    // Deactivating a user who still holds pending review tasks needs a replacement reviewer (ADR "Workflow
    // review reassignment"); the outcome tells the view to prompt for one and retry.
    public enum DeletePrincipalOutcome { Done, NeedsReplacementReviewer, Failed }

    public async Task<DeletePrincipalOutcome> DeleteSelectedPrincipalAsync()
    {
        if (_api is null || SelectedPrincipal is not { } p)
        {
            return DeletePrincipalOutcome.Failed;
        }

        try
        {
            if (p.IsGroup)
            {
                await _api.Admin.DeleteGroupAsync(p.Source!);
            }
            else
            {
                await _api.Admin.DeleteUserAsync(p.Source!);
            }

            Status = p.IsGroup ? "Group deleted." : "User deactivated.";
            SelectedPrincipal = null;
            await LoadPrincipalsAsync();
            return DeletePrincipalOutcome.Done;
        }
        catch (ReviewerHasPendingReviewsException)
        {
            return DeletePrincipalOutcome.NeedsReplacementReviewer; // keep SelectedPrincipal for the retry
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
            return DeletePrincipalOutcome.Failed;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrDelete");
            return DeletePrincipalOutcome.Failed;
        }
    }

    // Candidate replacement reviewers for a deactivation reassignment — active users other than the one being
    // deactivated (the currently-selected principal).
    public IReadOnlyList<(Guid Id, string Name)> ReplacementReviewerCandidates() =>
        SelectedPrincipal is { } p
            ? Principals.Where(x => !x.IsGroup && x.IsActive && x.Id != p.Id).Select(x => (x.Id, x.Name)).ToList()
            : [];

    // Retry the deactivation, handing the user's pending reviews to the chosen replacement.
    public async Task ReassignReviewsAndDeactivateAsync(Guid replacementId)
    {
        if (_api is null || SelectedPrincipal is not { } p)
        {
            return;
        }

        try
        {
            await _api.Admin.DeleteUserAsync(p.Source!, replacementId);
            Status = Strings.Get("StReviewsReassigned");
            SelectedPrincipal = null;
            await LoadPrincipalsAsync();
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrReassign");
        }
    }

    // ---- Workflow + tasks (ADR "Workflow / document state model", 0009) -------------------------------

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasTasks))] private int _taskCount;
    public bool HasTasks => TaskCount > 0;

    [RelayCommand]
    private Task RefreshTasks() => LoadTasksAsync();

    // Builds the view-model for the on-demand workflow window (ADR "Workflow start on demand"). Opened +
    // loaded by the code-behind (which owns the Window); null when there's no api client or no document
    // selected. The window drives Submit/Approve/Reject/Release against the selected document's latest
    // confirmed version.
    public WorkflowWindowViewModel? CreateWorkflowViewModel()
    {
        if (_api is null || SelectedItem is not { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false } item)
        {
            return null;
        }

        return new WorkflowWindowViewModel(_api, item.Href("versions"), item.DocumentSelfHref);
    }

    // ---- In-app notifications bell (ADR "Notification viewer + click-through") -----------------------
    public ObservableCollection<NotificationRowViewModel> Notifications { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasUnreadNotifications))] private int _unreadNotificationCount;
    public bool HasUnreadNotifications => UnreadNotificationCount > 0;

    public async Task LoadNotificationsAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var list = await _api.Notifications.GetNotificationsAsync();
            Notifications.Clear();
            foreach (var n in list.Items)
            {
                Notifications.Add(new NotificationRowViewModel(n));
            }

            UnreadNotificationCount = list.UnreadCount;
            _notificationsReadAllHref = list.ReadAllHref;
        }
        catch (Exception)
        {
            // best-effort — the bell just shows nothing
        }
    }

    // Live bell updates (ADR "Real-time notifications (SignalR)"): connect to the hub and reload the bell +
    // surface a status line whenever the server pushes a notification. Best-effort — the bell still loads on
    // login if the hub can't connect.
    private RealtimeNotificationClient? _realtime;

    private async Task StartRealtimeNotificationsAsync()
    {
        if (_api is null || _realtime is not null)
        {
            return;
        }

        try
        {
            _realtime = new RealtimeNotificationClient(DesktopClientOptions.ApiBaseUrl, _api.AccessToken);
            _realtime.NotificationReceived += n => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                Status = string.IsNullOrWhiteSpace(n.Title) ? n.Body : n.Title;
                await LoadNotificationsAsync();
            });
            await _realtime.StartAsync();
        }
        catch (Exception)
        {
            // real-time is best-effort; the bell still works via load-on-login.
        }
    }

    private async Task StopRealtimeNotificationsAsync()
    {
        if (_realtime is not null)
        {
            await _realtime.DisposeAsync();
            _realtime = null;
        }
    }

    [RelayCommand]
    private async Task MarkAllNotificationsRead()
    {
        if (_api is null)
        {
            return;
        }

        if (_notificationsReadAllHref is not { } readAllHref)
        {
            return;
        }

        try { await _api.Notifications.MarkAllNotificationsReadAsync(readAllHref); } catch (Exception) { }
        foreach (var n in Notifications) n.IsRead = true;
        UnreadNotificationCount = 0;
    }

    // Clicking a notification marks it read and, if it relates to a document, navigates to it.
    [RelayCommand]
    private async Task OpenNotification(NotificationRowViewModel? n)
    {
        if (n is null)
        {
            return;
        }

        if (_api is not null && !n.IsRead)
        {
            try { await _api.Notifications.MarkNotificationReadAsync(n.Notification); } catch (Exception) { }
            n.IsRead = true;
            if (UnreadNotificationCount > 0) UnreadNotificationCount--;
        }

        // Follow the row's `parent` (its home folder) and select the document there; a root document has no
        // parent, so its own `document` address opens as the folder (#443). Ids only for the row-matching.
        if (n.DocumentId is { } documentId && (n.Notification.Links?.GetValueOrDefault("parent") ?? n.Notification.Links?.GetValueOrDefault("document")) is { } href)
        {
            SelectedTab = 0; // Repositories
            await OpenFolderAsync(href, n.Notification.Links?.ContainsKey("parent") == true ? documentId : null);
        }
    }

    // Reloads the Tasks-tab list + badge — called after the workflow window closes, since a submit/approve/etc.
    // changes the caller's (or the reviewer's) pending-task set.
    public Task ReloadTasksAsync() => LoadTasksAsync();

    private async Task LoadTasksAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            Tasks.Clear();
            foreach (var t in await _api.Workflow.GetTasksAsync())
            {
                Tasks.Add(new TaskItemViewModel
                {
                    DocumentId = t.DocumentId,
                    ParentId = t.ParentId,
                    Links = t.Links,
                    DocumentName = t.DocumentName,
                    VersionNumber = t.VersionNumber,
                    AssignedAt = t.AssignedAt,
                    DueAt = t.DueAt,
                });
            }

            TaskCount = Tasks.Count;
            RebuildVisibleTasks();
        }
        catch (Exception)
        {
            TaskCount = 0;
        }
    }

    [RelayCommand]
    private async Task OpenTask(TaskItemViewModel? task)
    {
        if (task is null)
        {
            return;
        }

        // Follow the row's `parent` and select the document there; a root document opens itself (#443).
        if ((task.Links?.GetValueOrDefault("parent") ?? task.Links?.GetValueOrDefault("document")) is { } href)
        {
            SelectedTab = 0; // Repositories
            await OpenFolderAsync(href, task.Links?.ContainsKey("parent") == true ? task.DocumentId : null);
        }
    }

    // ---- Tag catalog admin (ADR "Tag controlled vocabulary") --------------------------------------------
    public ObservableCollection<TagCatalogRow> TagCatalogAdmin { get; } = [];
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string _newTagColor = "";

    private async Task LoadTagCatalogAsync()
    {
        if (_api is null)
        {
            return;
        }

        TagCatalogAdmin.Clear();
        try
        {
            foreach (var t in (await _api.Documents.GetTagCatalogWithColorsAsync()).Items)
            {
                TagCatalogAdmin.Add(new TagCatalogRow(t));
            }
        }
        catch (Exception) { /* not readable */ }
    }

    [RelayCommand]
    private async Task CreateTag()
    {
        if (_api is null || string.IsNullOrWhiteSpace(NewTagName))
        {
            return;
        }

        try
        {
            await _api.Documents.CreateTagAsync(NewTagName.Trim(), string.IsNullOrWhiteSpace(NewTagColor) ? null : NewTagColor.Trim());
            NewTagName = "";
            NewTagColor = "";
            await LoadTagCatalogAsync();
        }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not add the tag."; }
    }

    [RelayCommand]
    private async Task SaveTag(TagCatalogRow? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            await _api.Documents.UpdateTagAsync(row.Source, row.Name.Trim(), string.IsNullOrWhiteSpace(row.Color) ? "" : row.Color!.Trim());
            await LoadTagCatalogAsync();
        }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not update the tag."; }
    }

    [RelayCommand]
    private async Task RetireTag(TagCatalogRow? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try { await _api.Documents.RetireTagAsync(row.Source); await LoadTagCatalogAsync(); }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not retire the tag."; }
    }

    [RelayCommand]
    private async Task MergeTag(TagCatalogRow? row)
    {
        if (_api is null || row?.MergeTarget is not { } target || target.Id == row.Id)
        {
            return;
        }

        try { await _api.Documents.MergeTagAsync(row.Source, target.Id); await LoadTagCatalogAsync(); }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not merge the tags."; }
    }

    // ---- My work dashboard (ADR "My work dashboard") ------------------------------------------------------
    public ObservableCollection<RemindersClient.DashReminderInfo> DashboardReminders { get; } = [];
    public ObservableCollection<SimplArchiveApiClient.DashFollowedInfo> DashboardFollowing { get; } = [];

    private async Task LoadMyWorkAsync()
    {
        if (_api is not { } api)
        {
            return;
        }

        DashboardReminders.Clear();
        foreach (var r in await api.Reminders.GetDashboardRemindersAsync())
        {
            DashboardReminders.Add(r);
        }

        DashboardFollowing.Clear();
        foreach (var f in await api.GetDashboardFollowingAsync())
        {
            DashboardFollowing.Add(f);
        }

        await LoadTasksAsync();
    }

    [RelayCommand]
    private async Task OpenDashboardReminder(RemindersClient.DashReminderInfo? row)
    {
        if (row is null)
        {
            return;
        }

        // Follow the row's `parent` and select the document there; a root document opens itself (#443).
        if ((row.Links?.GetValueOrDefault("parent") ?? row.Links?.GetValueOrDefault("document")) is { } href)
        {
            SelectedTab = 0;
            await OpenFolderAsync(href, row.Links?.ContainsKey("parent") == true ? row.DocumentId : null);
        }
    }

    [RelayCommand]
    private async Task OpenDashboardFollowed(SimplArchiveApiClient.DashFollowedInfo? row)
    {
        if (row is null)
        {
            return;
        }

        if ((row.Links?.GetValueOrDefault("parent") ?? row.Links?.GetValueOrDefault("document")) is { } href)
        {
            SelectedTab = 0;
            await OpenFolderAsync(href, row.Links?.ContainsKey("parent") == true ? row.DocumentId : null);
        }
    }

    // Loads the always-shown system fields for the selected document (ADR "System fields + OCR-language mask
    // field"). OCR languages only apply to a TIFF-sourced document.
    private async Task LoadSystemFieldsAsync(string documentSelfHref, string versionsHref, string name)
    {
        SysName = name;
        SysDocumentDate = null;
        SysCreated = "";
        SysCreatedBy = "";
        SysWorkflowStatus = null;
        SysFileExtension = "";
        SysHasTiff = false;
        SysOcrLanguages = "";
        SysCurrentVersion = "";
        _sysOcrCodes = [];
        _stagedOcrCodes = [];

        if (_api is null)
        {
            return;
        }

        // Sensitivity label applies to any document (ADR "Configurable sensitivity labels + upload defaults") —
        // load it before the version-less early-return so a folder can show/edit it too.
        try
        {
            // One read of the document resource serves the label AND the external-links rel (issue #385).
            var detail = await _api.Documents.GetDocumentDetailAsync(documentSelfHref);
            var s = detail.Sensitivity;
            _detailSensitivityName = s.Name;
            _detailSensitivityColor = s.Color;
            _detailSensitivityWatermark = s.Watermark;
            DetailSensitivityId = s.LabelId;
            _detailExternalLinksHref = detail.ExternalLinksHref;
            // The rels this resource advertised, so the calls below follow addresses instead of composing
            // them from the id (ADR 0543, issue #416). Captured here because `detail` is scoped to this try.
            _detailLinks = detail.Links;
            _detailDocumentName = detail.Name;
            CanShareDocument = detail.ExternalLinksHref is not null;
            // Folder-only, and read from the resource because a child folder's order is never fetched by the
            // parent's listing that opened this pane (issue #408).
            _detailSortOrder = detail.ContentsSortOrder;
            OnPropertyChanged(nameof(DetailSortText));
        }
        catch (Exception) { _detailSensitivityName = ""; _detailSensitivityColor = null; _detailSensitivityWatermark = false; DetailSensitivityId = null; _detailExternalLinksHref = null; CanShareDocument = false; }
        // Sensitivity watermark on the preview (ADR "Document watermarking") — when the label's watermark flag is set.
        Preview.WatermarkText = _detailSensitivityWatermark ? $"{_detailSensitivityName} · {UserDisplayName}" : "";
        // Whether the current user follows this document (ADR "Document subscriptions").
        try { DetailSubscribed = await _api.Reminders.GetSubscriptionAsync(DetailHref("subscription")); } catch (Exception) { DetailSubscribed = false; }

        // Free-form tags (ADR "Document tags").
        DetailTags.Clear();
        try { foreach (var t in await _api.Documents.GetTagsAsync(DetailHref("tags"))) DetailTags.Add(t); } catch (Exception) { /* leave empty */ }
        HasDetailTags = DetailTags.Count > 0;

        var fields = await _api.Documents.GetSystemFieldsAsync(versionsHref);
        if (fields is null)
        {
            return; // no confirmed version yet (e.g. a folder)
        }

        _sysCurrentVersionId = fields.CurrentVersionId;
        _sysDocumentDateHref = fields.DocumentDateHref;
        SysCurrentVersion = fields.CurrentVersionNumber.ToString();
        SysCreated = fields.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        SysCreatedBy = fields.CreatedByName;
        SysWorkflowStatus = fields.WorkflowStatus;
        SysFileExtension = fields.FileExtension;
        SysDocumentDate = DateTimeOffset.TryParse(fields.DocumentDate, out var d) ? new DateTimeOffset(d.Date, TimeSpan.Zero) : null;
        SysHasTiff = fields.HasTiffVersion;

        if (SysHasTiff)
        {
            if (_ocrCatalog.Count == 0)
            {
                try { _ocrCatalog = await _api.GetOcrLanguageCatalogAsync(); }
                catch (Exception) { /* leave empty */ }
            }

            _sysOcrCodes = string.IsNullOrWhiteSpace(fields.OcrLanguages) ? [] : fields.OcrLanguages.Split('+', StringSplitOptions.RemoveEmptyEntries);
            _stagedOcrCodes = _sysOcrCodes;
            SysOcrLanguages = DescribeOcrLanguages(_sysOcrCodes);
        }
    }

    // Turns ordered codes into a readable, priority-ordered display ("German, French"); empty = tenant default.
    private string DescribeOcrLanguages(IReadOnlyList<string> codes)
    {
        if (codes.Count == 0)
        {
            return "(tenant default)";
        }

        return string.Join(", ", codes.Select(c => _ocrCatalog.FirstOrDefault(o => o.Code == c)?.DisplayName ?? c));
    }

    // Exposes the catalog + the currently staged ordered selection to the picker dialog (the view owns the
    // dialog). The picker stages into the pane; the pane's single Save persists it.
    public (IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> Catalog, IReadOnlyList<string> Selected) OcrLanguagePickerState() =>
        (_ocrCatalog, _stagedOcrCodes);

    // Stages the picker's ordered selection (no API call) — persisted by SaveDetail, discarded by cancel.
    public void StageOcrLanguages(IReadOnlyList<string> codes)
    {
        _stagedOcrCodes = codes;
        SysOcrLanguages = DescribeOcrLanguages(codes);
    }

    // ---- Editable detail pane (ADR "Single pane-level edit toggle on the detail pane") ---------------------
    // One edit mode governs the whole pane: read-only until Edit; every read-write field (Name, Document date,
    // OCR languages, mask + index data) becomes editable at once; one Save persists only the changed fields,
    // one Cancel discards them.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOcr))]
    [NotifyPropertyChangedFor(nameof(CanBeginEdit))]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeginEdit))]
    private bool _canEditDetail; // a document detail (not the repository-list root) is loaded

    [ObservableProperty] private MaskChoiceViewModel? _selectedMaskChoice;

    // Edit affordances: begin only when a detail is loaded and not already editing; the OCR picker only for a
    // TIFF-sourced document and only while editing.
    public bool CanBeginEdit => CanEditDetail && !IsEditing;

    // Seeds the staged contents order from the subject, so opening the edit does not silently change it.
    private void StageSortOrder() => EditSortOrder = _detailSortOrder;
    public bool CanEditOcr => IsEditing && SysHasTiff;

    public ObservableCollection<MaskChoiceViewModel> AvailableMasks { get; } = [];
    public ObservableCollection<MaskFieldEditViewModel> MaskEditFields { get; } = [];

    private Guid? _originalMaskId;
    private string _originalName = "";
    private DateTimeOffset? _originalDocumentDate;
    private bool _loadingMaskEdit;

    [RelayCommand]
    private async Task BeginEditAsync()
    {
        if (_api is null || _selectedDocumentId is not { } documentId)
        {
            return;
        }

        try
        {
            _loadingMaskEdit = true;
            StageSortOrder();

            // Snapshot the read-write system fields so Save can persist only what actually changed and Cancel
            // can restore them.
            _originalName = SysName;
            _originalDocumentDate = SysDocumentDate;
            _stagedOcrCodes = _sysOcrCodes;
            RebuildSensitivityPicker();
            SelectedSensitivityItem = SensitivityPickerItems.FirstOrDefault(i => i.Id == DetailSensitivityId) ?? SensitivityPickerItems.FirstOrDefault();

            // Tags (ADR "Document tags"): working copy + original for change detection + the tenant catalog.
            EditTags.Clear();
            foreach (var t in DetailTags) EditTags.Add(t);
            _origTags = [.. DetailTags];
            NewTag = "";
            if (TagCatalog.Count == 0)
            {
                try { foreach (var t in await _api.Documents.GetTagCatalogAsync()) TagCatalog.Add(t); } catch (Exception) { /* optional */ }
            }

            AvailableMasks.Clear();
            AvailableMasks.Add(new MaskChoiceViewModel(null, "(No mask)"));
            foreach (var mask in await _api.Documents.GetMasksAsync())
            {
                AvailableMasks.Add(new MaskChoiceViewModel(mask.Id, mask.Name, mask));
            }

            _originalMaskId = (await _api.Documents.GetMaskAsync(DetailHref("mask"))).MaskId;
            SelectedMaskChoice = AvailableMasks.FirstOrDefault(m => m.MaskId == _originalMaskId) ?? AvailableMasks[0];
            await LoadMaskEditFieldsAsync(SelectedMaskChoice.Mask, withCurrentValues: true);

            IsEditing = true;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrStartEdit"), e.Message);
        }
        finally
        {
            _loadingMaskEdit = false;
        }
    }

    // Re-load the field editors when the user picks a different mask (empty values — a different mask has
    // different fields). Suppressed during the initial edit load, which fills the current values instead.
    async partial void OnSelectedMaskChoiceChanged(MaskChoiceViewModel? value)
    {
        if (_loadingMaskEdit || _selectedDocumentId is not { } documentId)
        {
            return;
        }

        await LoadMaskEditFieldsAsync(value?.Mask, withCurrentValues: false);
    }

    private async Task LoadMaskEditFieldsAsync(DocumentsClient.MaskOptionInfo? mask, bool withCurrentValues)
    {
        MaskEditFields.Clear();
        if (_api is null || mask is not { } chosen)
        {
            return;
        }

        var fields = await _api.Documents.GetMaskFieldsAsync(chosen);
        var valuesByName = withCurrentValues
            ? (await _api.Documents.GetIndexDataAsync(DetailHref("index-data"))).ToDictionary(f => f.FieldName, f => f.Values)
            : new Dictionary<string, IReadOnlyList<string>>();

        foreach (var field in fields)
        {
            var values = valuesByName.TryGetValue(field.Name, out var v) ? v : [];
            MaskEditFields.Add(MaskFieldEditViewModel.Create(field, values));
        }
    }

    // Persists every read-write field that actually changed. Each field is independent, so one failure doesn't
    // abort the others; if anything failed we stay in edit mode and report it, otherwise we drop to read-only.
    [RelayCommand]
    private async Task SaveDetailAsync()
    {
        // IsEditing guards the KEYBOARD path (ADR 0550): Ctrl/Cmd+S is bound window-wide, so without this a
        // save would fire while the pane is merely displaying a document.
        if (_api is null || !IsEditing || _selectedDocumentId is not { } documentId)
        {
            return;
        }

        var failures = new List<string>();
        var nameChanged = false;

        // Name (rename).
        var newName = SysName?.Trim() ?? "";
        if (newName.Length > 0 && newName != _originalName)
        {
            try
            {
                await _api.Documents.RenameAsync(DetailHref("self"), newName);
                DetailTitle = newName;
                _originalName = newName;
                nameChanged = true;
            }
            catch (Exception e) { failures.Add($"name ({e.Message})"); }
        }

        // Document date (on the current version).
        if (_sysDocumentDateHref is { } dateHref && SysDocumentDate is { } date && date != _originalDocumentDate)
        {
            try
            {
                await _api.Versions.SetDocumentDateAsync(dateHref, date.ToString("yyyy-MM-dd"));
                _originalDocumentDate = date;
            }
            catch (Exception e) { failures.Add($"document date ({e.Message})"); }
        }

        // OCR languages (only if the ordered selection changed — this re-runs the searchable-PDF conversion).
        if (SysHasTiff && !_stagedOcrCodes.SequenceEqual(_sysOcrCodes))
        {
            try
            {
                await _api.Documents.SetOcrLanguagesAsync(DetailHref("ocr-languages"), _stagedOcrCodes);
                _sysOcrCodes = _stagedOcrCodes;
            }
            catch (Exception e) { failures.Add($"OCR languages ({e.Message})"); }
        }

        // Sensitivity label (ADR "Configurable sensitivity labels + upload defaults").
        var chosenLabelId = SelectedSensitivityItem?.Id;
        if (chosenLabelId != DetailSensitivityId)
        {
            try
            {
                await _api.Documents.SetSensitivityAsync(DetailHref("sensitivity"), chosenLabelId);
                var lbl = SensitivityCatalog.FirstOrDefault(l => l.Id == chosenLabelId);
                _detailSensitivityName = lbl?.Name ?? "";
                _detailSensitivityColor = lbl?.Color;
                _detailSensitivityWatermark = lbl?.Watermark ?? false;
                DetailSensitivityId = chosenLabelId;
                Preview.WatermarkText = _detailSensitivityWatermark ? $"{_detailSensitivityName} · {UserDisplayName}" : "";
            }
            catch (Exception e) { failures.Add($"sensitivity ({e.Message})"); }
        }

        // Free-form tags (ADR "Document tags"): PUT-replaces the whole set (the server normalizes/dedupes).
        var editTags = EditTags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 100).Distinct().ToList();
        if (!editTags.OrderBy(t => t).SequenceEqual(_origTags.OrderBy(t => t)))
        {
            try
            {
                var stored = await _api.Documents.SetTagsAsync(DetailHref("tags"), editTags);
                DetailTags.Clear();
                foreach (var t in stored) DetailTags.Add(t);
                HasDetailTags = DetailTags.Count > 0;
                _origTags = [.. DetailTags];
            }
            catch (Exception e) { failures.Add($"tags ({e.Message})"); }
        }

        // Mask + index data.
        try
        {
            var newMaskId = SelectedMaskChoice?.MaskId;
            if (newMaskId is null)
            {
                if (_originalMaskId is not null)
                {
                    await _api.Documents.ClearMaskAsync(DetailHref("mask"));
                    _originalMaskId = null;
                }
            }
            else
            {
                // Fill index data first, then (re)assign the mask — assigning re-checks required fields, so
                // the values must already be in place (ADR "Document metadata (index data) endpoints").
                await _api.Documents.SetIndexDataAsync(DetailHref("index-data"), MaskEditFields.Select(f => (f.FieldDefinitionId, f.ToValues())));
                if (newMaskId != _originalMaskId)
                {
                    await _api.Documents.SetMaskAsync(DetailHref("mask"), newMaskId.Value);
                    _originalMaskId = newMaskId;
                }
            }
        }
        catch (ApiActionException e) { failures.Add(e.Message); } // required field missing / invalid value
        catch (Exception e) { failures.Add($"mask ({e.Message})"); }

        // A folder's contents order commits with everything else, from the same Save (issue #408). Skipped for a
        // document, which lists nothing, and when unchanged — so an ordinary edit sends no extra request.
        if (_detailIsFolder && EditSortOrder != _detailSortOrder)
        {
            try
            {
                await _api.Documents.SetContentsSortOrderAsync(DetailHref("contents-sort-order"), EditSortOrder);
                _detailSortOrder = EditSortOrder;
                OnPropertyChanged(nameof(DetailSortText));

                // The OPEN folder's listing re-sorts only when it is the folder that changed.
                if (_currentFolderId == documentId)
                {
                    _folderSortOrder = EditSortOrder;
                    _headerSortActive = false;
                }
            }
            catch (Exception e) { failures.Add($"contents sort order ({e.Message})"); }
        }

        if (failures.Count > 0)
        {
            Status = string.Format(Strings.Get("StErrSaveJoin"), string.Join("; ", failures));
            return; // stay in edit mode so the user can correct the rejected field(s)
        }

        IsEditing = false;
        Status = Strings.Get("StSaved");
        await ReloadDetailAsync();
        if (nameChanged)
        {
            await ReloadTreeAsync();
            await LoadFolderContentsAsync(_currentFolderId ?? documentId);
        }
    }

    [RelayCommand]
    private async Task CancelEditAsync()
    {
        // Esc is bound window-wide and also exits the preview full-screen (ADR 0550), so this must do nothing
        // unless the pane is actually editing — the two states never coexist, and each command ignores the one
        // it does not own.
        if (!IsEditing)
        {
            return;
        }

        IsEditing = false;

        // Restore the staged system fields to their loaded values.
        SysName = _originalName;
        SysDocumentDate = _originalDocumentDate;
        _stagedOcrCodes = _sysOcrCodes;
        SysOcrLanguages = DescribeOcrLanguages(_sysOcrCodes);

        if (_selectedDocumentId is not null)
        {
            await ReloadDetailAsync();
        }
    }

    // Reloads the read-only mask line + index fields after a save/cancel.
    private async Task ReloadDetailAsync()
    {
        if (_api is null)
        {
            return;
        }

        var mask = await _api.Documents.GetMaskAsync(DetailHref("mask"));
        MaskLine = mask.Name is null ? "No mask" : $"Mask: {mask.Name}" + (mask.VersionNumber is { } v ? $" · version {v}" : "");

        IndexFields.Clear();
        foreach (var field in await _api.Documents.GetIndexDataAsync(DetailHref("index-data")))
        {
            IndexFields.Add(new IndexFieldViewModel { FieldName = field.FieldName, Values = string.Join(", ", field.Values) });
        }
    }

    // The Repositories/Intray preview render goes through the shared Preview surface (ADR "Desktop recycle bin
    // parity" — the Recycle bin has its own).
    private async Task LoadPreviewAsync(string versionsHref) => await Preview.RenderAsync(await _api!.Documents.GetPreviewAsync(versionsHref));

    // ---- Author identity card (ADR 0544) -------------------------------------------------------------

    // The card currently shown in the author flyout. One at a time, so a single property serves every message.
    [ObservableProperty]
    private UserCardViewModel? _authorCard;

    [ObservableProperty]
    private bool _authorCardFailed;

    // Opens the card for a message's author by FOLLOWING the href the message advertised — the client never
    // builds that URL (ADR 0543). Only reachable from a message that has a card: an automation's name is not
    // a button at all.
    [RelayCommand]
    private async Task ShowAuthorCardAsync(ChatMessageViewModel? message)
    {
        AuthorCard = null;
        AuthorCardFailed = false;

        if (message?.AuthorCardHref is not { } href || _api is null)
        {
            return;
        }

        var loaded = await _api.Profile.GetUserCardAsync(href);
        if (loaded is not { } result)
        {
            AuthorCardFailed = true;
            return;
        }

        AuthorCard = new UserCardViewModel
        {
            DisplayName = result.Card.DisplayName,
            Email = result.Card.Email,
            IsActive = result.Card.IsActive,
            Photo = result.Photo,
        };
    }

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

    private string _detailDocumentName = "";

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

    private async Task LoadCommentsAsync(string chatHref)
    {
        var thread = await _api!.Documents.GetChatAsync(chatHref);
        var comments = thread.Messages;
        _mentionableUsersHref = thread.MentionableUsersHref;
        MentionCandidates.Clear();
        HasMentionCandidates = false;
        var byId = comments.ToDictionary(
            c => c.Id,
            c => new ChatMessageViewModel { Id = c.Id, AuthorName = c.AuthorName, Body = c.Body, CreatedAt = c.CreatedAt, AuthorCardHref = c.AuthorCardHref, Kind = c.Kind, VersionNumber = c.VersionNumber, VersionComment = c.VersionComment, VersionCommentKind = c.VersionCommentKind, CanReply = c.ParentMessageId is null, Mentions = c.Mentions });

        Comments.Clear();
        foreach (var comment in comments.Where(c => c.ParentMessageId is null))
        {
            var vm = byId[comment.Id];
            foreach (var reply in comments.Where(c => c.ParentMessageId == comment.Id))
            {
                vm.Replies.Add(byId[reply.Id]);
            }

            Comments.Add(vm);
        }
    }

    // Opens the inline reply box under one message, closing whichever was open — one conversation at a time, the
    // same rule the web client follows. Re-clicking the same message closes it, and either way the half-typed
    // text is dropped: a reply is addressed to a specific message, so carrying it to another one would misfile it.
    [RelayCommand]
    private void ToggleReply(ChatMessageViewModel? message)
    {
        if (message is null)
        {
            return;
        }

        var opening = !message.IsReplying;

        foreach (var other in Comments)
        {
            other.IsReplying = false;
            other.ReplyText = "";
        }

        message.IsReplying = opening;
    }

    [RelayCommand]
    private async Task PostReplyAsync(ChatMessageViewModel? message)
    {
        if (_api is null || _selectedDocumentId is not { } documentId
            || message is null || string.IsNullOrWhiteSpace(message.ReplyText))
        {
            return;
        }

        try
        {
            await _api.Documents.PostCommentAsync(DetailHref("chat"), message.ReplyText, parentCommentId: message.Id);

            // Reloading rebuilds the collection, so the open reply box disappears with it — no need to reset the
            // flag on an instance that is about to be replaced.
            await LoadCommentsAsync(DetailHref("chat"));
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrPostComment"), e.Message);
        }
    }

    [RelayCommand]
    private async Task PostCommentAsync()
    {
        if (_api is null || _selectedDocumentId is not { } documentId || string.IsNullOrWhiteSpace(NewComment))
        {
            return;
        }

        try
        {
            await _api.Documents.PostCommentAsync(DetailHref("chat"), NewComment, parentCommentId: null);
            NewComment = "";
            await LoadCommentsAsync(DetailHref("chat"));
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrPostComment"), e.Message);
        }
    }

    private void ClearDetail()
    {
        _selectedDocumentId = null;
        DetailTitle = "";
        MaskLine = "";
        _detailSensitivityName = "";
        _detailSensitivityColor = null;
        _detailSensitivityWatermark = false;
        DetailSensitivityId = null;
        DetailSubscribed = false;
        DetailTags.Clear();
        HasDetailTags = false;
        IndexFields.Clear();
        Comments.Clear();
        Preview.WatermarkText = "";
        Preview.Reset("Select a document.");
        Preview.PreviewConverted = false;
        SysName = "";
        SysDocumentDate = null;
        SysCreated = "";
        SysCreatedBy = "";
        SysWorkflowStatus = null;
        SysFileExtension = "";
        SysHasTiff = false;
        SysOcrLanguages = "";
        SysCurrentVersion = "";
        _sysOcrCodes = [];
        _stagedOcrCodes = [];
        _sysCurrentVersionId = Guid.Empty;
        _sysDocumentDateHref = null;
        IsEditing = false;
        CanEditDetail = false;
        MaskEditFields.Clear();
        AvailableMasks.Clear();
    }

    private bool HasSelection() => SelectedItem is not null;

    // Populates a representative logged-in workbench for the headless UI screenshot (no network).
    internal void PopulateDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserEmail = "demo@simplarchive.local";
        CanCreateFolder = true;
        IsTenantAdmin = true;
        // An unread notification, so the bell badge is IN the demo renders. It clipped on the right for months
        // and no screenshot could have shown it, because no captured state ever had an unread count.
        UnreadNotificationCount = 2;
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Demo Repository", FolderId = Guid.NewGuid(), ShowSeparator = true });
        // Mirror the real tree's top-level nodes: a Personal repository (ADR 0370) and, for a tenant admin, the
        // synthetic Administration branch (ADR 0377), around the shared repositories.
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Personal", true, null, isPersonal: true));
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Demo Repository", true, null));
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Invoices", false, null, hasChildren: false)); // an EMPTY folder — shows the pastel glyph (ADR "Empty-folder tree icon")
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Shared (ref)", false, null, isReference: true));
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Administration", true, null, syntheticIcon: "mdi-shield-account"));
        Items.Add(new NodeViewModel { Id = Guid.Empty, Name = "Invoices", HasChildren = true, HasVersions = false });
        Items.Add(new NodeViewModel { Id = Guid.Empty, Name = "Invoice 2025-001.pdf", HasChildren = false, HasVersions = true });
        Items.Add(new NodeViewModel { Id = Guid.Empty, Name = "sample.docx", HasChildren = false, HasVersions = true });
        Items.Add(new NodeViewModel { Id = Guid.Empty, Name = "Shared Contract.pdf", HasChildren = false, HasVersions = true, IsReference = true });
        SelectedItem = Items[1]; // a document is picked, so Rename/Delete/Download are enabled in the screenshot
        DetailTitle = "Invoice 2025-001";
        SysName = "Invoice 2025-001";
        SysFileExtension = ".pdf";
        SysCreated = "2026-07-15 09:12";
        SysCreatedBy = "Demo Admin";
        SysDocumentDate = new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero);
        SysHasTiff = false;
        SysOcrLanguages = "German, French";
        MaskLine = "Mask: Basic Entry · version 1";
        CanEditDetail = true;
        IndexFields.Add(new IndexFieldViewModel { FieldName = "Keywords", Values = "invoice, reviewed" });
        Preview.PreviewConverted = false;
        Preview.Reset("Preview renders here (PDF/image/text).");
        // The thread mixes what the product records AUTOMATICALLY (ADR 0545) with what a person typed — which is
        // what a real feed looks like. The fixture previously held only the typed comment, so the manual showed a
        // chat pane that the product no longer produces.
        //
        // Note this fixture is synthetic: it does not come from the demo seed, so it does not follow the product
        // on its own. It has to be updated by hand whenever the thread gains something new — see the backlog entry
        // on the desktop capture being fixture-driven.
        // ONE entry for the filing, not two: a first version IS the document arriving, and it carries the version
        // chip and check-in comment (ADR 0545). The fixture used to hold the separate "filed a new document"
        // entry beside this one, which is exactly the duplication the product stopped producing.
        Comments.Add(new ChatMessageViewModel
        {
            Id = Guid.Empty,
            AuthorName = "Demo Admin",
            Body = "",
            Kind = 1,
            VersionNumber = 1,
            VersionComment = "Scanned from the paper original.",
            CreatedAt = ScreenshotClock,
        });
        // "Demo Admin", not the email address: the author label became DisplayName when identity cards landed
        // (ADR 0544), and this fixture still showed the raw email the product used to render.
        //
        // CanReply + a reply of its own, so the capture shows the thread the product can actually produce
        // (issue #383) rather than a flat list — the affordance is on the message in the manual because it is on
        // the message in the app.
        var typed = new ChatMessageViewModel
        {
            Id = Guid.Empty,
            AuthorName = "Demo Admin",
            Body = "Looks good.",
            CreatedAt = ScreenshotClock,
            CanReply = true,
        };
        typed.Replies.Add(new ChatMessageViewModel
        {
            Id = Guid.Empty,
            AuthorName = "Demo Admin",
            Body = "Filed under Invoices.",
            CreatedAt = ScreenshotClock,
        });
        Comments.Add(typed);
        Status = "3 item(s).";
    }

    // Populates the Tasks tab for the headless screenshot (ADR "Workflow / document state model", 0009). The
    // workflow itself is now a separate on-demand window (ADR "Workflow start on demand"), so it isn't part of
    // this main-window screenshot.
    internal void PopulateWorkflowDemoForScreenshot()
    {
        PopulateDemoForScreenshot();

        Tasks.Add(new TaskItemViewModel { DocumentId = Guid.NewGuid(), DocumentName = "Q3 Invoice.pdf", VersionNumber = 2, AssignedAt = ScreenshotClock.AddHours(-3), DueAt = ScreenshotClock.AddDays(-1) });
        Tasks.Add(new TaskItemViewModel { DocumentId = Guid.NewGuid(), DocumentName = "Vendor Contract.docx", VersionNumber = 1, AssignedAt = ScreenshotClock.AddDays(-1), DueAt = ScreenshotClock.AddDays(3) });
        TaskCount = Tasks.Count;
        RebuildVisibleTasks();
    }

    // Populates the pane edit mode (a document selected, Edit pressed) for the headless screenshot — the whole
    // pane is editable: system fields (Name/Document date/OCR languages) plus the mask + index fields.
    internal void PopulateMaskEditForScreenshot()
    {
        PopulateDemoForScreenshot();
        AvailableMasks.Add(new MaskChoiceViewModel(null, "(No mask)"));
        AvailableMasks.Add(new MaskChoiceViewModel(Guid.NewGuid(), "Basic Entry"));
        AvailableMasks.Add(new MaskChoiceViewModel(Guid.NewGuid(), "eMail"));
        SelectedMaskChoice = AvailableMasks[1];
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new DocumentsClient.MaskFieldInfo(Guid.NewGuid(), "Keywords", "MultiSelect", false), ["finance", "quarterly"]));
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new DocumentsClient.MaskFieldInfo(Guid.NewGuid(), "Amount", "Number", true), ["1240"]));
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new DocumentsClient.MaskFieldInfo(Guid.NewGuid(), "Due date", "Date", false), ["2026-07-28"]));
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new DocumentsClient.MaskFieldInfo(Guid.NewGuid(), "Paid", "Boolean", false), ["true"]));
        IsEditing = true;
    }

    // Headless exercise of breadcrumb building/navigation against a running Api (see Program --breadcrumb-test).
    internal async Task<List<string>> BreadcrumbSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var trail = new List<string>();

        await LoadRootAsync();
        trail.Add(BreadcrumbTrail());

        var repositories = await _api!.Documents.GetRepositoriesAsync();
        var repositoryNode = new TreeNodeViewModel(repositories[0].Id, repositories[0].Name, repositories[0].HasSubfolders, LoadTreeChildrenAsync, links: repositories[0].Links);
        SetBreadcrumbFromTreeNode(repositoryNode);
        await LoadFolderContentsAsync(repositoryNode.Id, repositoryNode.Links);
        trail.Add(BreadcrumbTrail());

        if (Items.FirstOrDefault(i => i.IsFolder) is { } folder)
        {
            Breadcrumbs.Add(new BreadcrumbViewModel { Name = folder.Name, FolderId = folder.Id, ShowSeparator = true });
            await LoadFolderContentsAsync(folder.Id, folder.Links);
            trail.Add(BreadcrumbTrail());

            // Click the repository crumb (index 1) to navigate back up.
            await NavigateToBreadcrumbCommand.ExecuteAsync(Breadcrumbs[1]);
            trail.Add(BreadcrumbTrail());
        }

        return trail;
    }

    private string BreadcrumbTrail() => string.Join(" / ", Breadcrumbs.Select(b => b.Name)) + $"  [{Items.Count} items]";

    // Populates the Search tab for the headless screenshot (no network).
    internal void PopulateSearchDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserEmail = "demo@simplarchive.local";
        SelectedTab = 3;
        SearchQuery = "invoice";
        SearchResults.Add(new SearchResultViewModel { Id = Guid.Empty, Name = "Zeta Invoice.pdf", IsFolder = false, ParentId = Guid.Empty, Path = "Repositories / Demo Repository / Invoices", Highlight = "…total amount due of CHF 1'240 for <em>invoice</em> number 2026-03, payable within 30 days…" });
        SearchResults.Add(new SearchResultViewModel { Id = Guid.Empty, Name = "Invoices", IsFolder = true, ParentId = Guid.Empty, Path = "Repositories / Demo Repository" });
        SearchResults.Add(new SearchResultViewModel { Id = Guid.Empty, Name = "March invoice run.xlsx", IsFolder = false, ParentId = Guid.Empty, Path = "Repositories / Demo Repository / 2026", Highlight = "Keywords: <em>invoice</em>, finance, 2026" });
        SearchStatus = "3 result(s).";

        // Show the refinement panel (ADR "Search-refinement UI", phase 2) populated, for the screenshot.
        SearchRepositories.Add(new SearchRepoOption(null, "All repositories"));
        SearchRepositories.Add(new SearchRepoOption(Guid.NewGuid(), "Demo Repository"));
        SelectedSearchRepository = SearchRepositories[0];
        _availableFieldNames = ["Amount", "Keywords", "Status"];
        _fieldTypes = new Dictionary<string, int> { ["Amount"] = 1, ["Keywords"] = 0, ["Status"] = 4 };
        CreatedByFilter = "Demo Admin";
        FieldFilters.Add(new FieldFilterRowViewModel(_availableFieldNames, _fieldTypes) { FieldName = "Amount", Value = "100" });
        FiltersExpanded = true;
    }

    internal void PopulateIntrayDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserEmail = "demo@simplarchive.local";
        SelectedTab = 1;
        ServerIntray.Add(new IntrayItemViewModel { Name = "invoice-2026-03.pdf", Size = 132_004, DownloadUrl = "", HasMask = true });
        ServerIntray.Add(new IntrayItemViewModel { Name = "meeting-notes.eml", Size = 8_942, DownloadUrl = "", HasMask = false });
        IntrayStatus = "2 item(s).";

        // Focus the first server item so the right panes (mask + preview) show in the screenshot.
        IntrayDetailTitle = "invoice-2026-03.pdf";
        IntrayItemFocused = true;
        IntrayName = "invoice-2026-03";
        IntrayDocumentDate = new DateTime(2026, 3, 31);
        IntrayAvailableMasks.Add(new MaskChoiceViewModel(null, "(No mask)"));
        IntrayAvailableMasks.Add(new MaskChoiceViewModel(Guid.NewGuid(), "Basic Entry"));
        IntraySelectedMaskChoice = IntrayAvailableMasks[1];
        IntrayMaskEditFields.Add(MaskFieldEditViewModel.Create(new DocumentsClient.MaskFieldInfo(Guid.NewGuid(), "Keywords", "MultiSelect", false), ["invoice", "march"]));
        Preview.Reset("Preview renders here (PDF/image/text).");
    }

    // Headless exercise of referenced folders appearing in the tree (see Program --reftree-test): references
    // a folder into another folder, then confirms the tree's child loader returns a shortcut node for it
    // whose Id is the target (so it expands the target's subtree).
    internal async Task<List<string>> RefTreeSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var log = new List<string>();

        var root = (await _api!.Documents.GetRepositoriesAsync())[0];
        var s = Guid.NewGuid().ToString("N")[..6];
        await _api.Documents.CreateFolderAsync(root.Href("children"), $"rtree-A-{s}");
        await _api.Documents.CreateFolderAsync(root.Href("children"), $"rtree-B-{s}");
        var a = (await _api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"rtree-A-{s}");
        var b = (await _api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"rtree-B-{s}");
        await _api.Documents.CreateFolderAsync(a.Href("children"), $"rtree-F-{s}");
        var f = (await _api.Documents.GetChildrenAsync(a.Href("children"))).First(c => c.Name == $"rtree-F-{s}");

        await _api.Documents.CreateReferenceAsync(b.Href("references"), f.Id);

        var bTreeChildren = (await LoadTreeChildrenAsync(new TreeNodeViewModel(b.Id, b.Name, false, null, links: b.Links))).ToList();
        var refNode = bTreeChildren.FirstOrDefault(n => n.IsReference);
        log.Add(refNode is not null && refNode.Id == f.Id && refNode.IconValue == "mdi-folder-arrow-right"
            ? "OK: referenced folder appears in the tree as a shortcut node targeting F."
            : "FAILED: referenced folder missing from the tree.");

        await _api.Documents.DeleteAsync(a.Href("self"));
        await _api.Documents.DeleteAsync(b.Href("self"));
        return log;
    }

    // Headless exercise of the tree refresh (see Program --treerefresh-test): creates a sub-folder inside the
    // first repository, then confirms the rebuilt tree's lazy-loader returns fresh children including it, and
    // that Refresh repopulates the tree.
    internal async Task<List<string>> TreeRefreshSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var log = new List<string>();

        await LoadRootAsync();
        log.Add($"tree roots: {Tree.Count}");

        var repository = (await _api!.Documents.GetRepositoriesAsync())[0];
        var name = $"treetest-{Guid.NewGuid():N}"[..16];
        await LoadFolderContentsAsync(repository.Id, repository.Links);
        await CreateFolderAsync(name);

        var treeChildren = (await LoadTreeChildrenAsync(new TreeNodeViewModel(repository.Id, repository.Name, false, null, links: repository.Links))).Select(n => n.Name).ToList();
        log.Add(treeChildren.Contains(name) ? "OK: rebuilt tree loader returns the new folder." : "FAILED: new folder missing from tree.");

        Tree.Clear();
        await RefreshCommand.ExecuteAsync(null);
        log.Add(Tree.Count > 0 ? "OK: Refresh repopulated the tree." : "FAILED: Refresh left the tree empty.");

        // Clean up the test folder.
        var created = (await _api.Documents.GetChildrenAsync(repository.Href("children"))).First(c => c.Name == name);
        await _api.Documents.DeleteAsync(created.Href("self"));
        return log;
    }

    // Headless regression for the tree-select desync bugfix (see DesktopTreeSelectTests): after drilling into a
    // subfolder via the contents list, the tree's selected node is unchanged, so re-tapping it must STILL
    // reload the list — the [ObservableProperty] SelectedTreeNode setter short-circuits a same-reference
    // re-selection (so OnSelectedTreeNodeChanged never fires), and ReselectTreeFolderAsync (the Tapped
    // handler's target) is what closes that gap. Ordering is controlled here so the async-void selection
    // handler can't race the deterministic loads. Returns the folder shown after the list-drill and after the
    // re-tap, plus the repo's re-listed item names.
    internal async Task<(Guid AfterDrill, Guid AfterRetap, string[] Items)> TreeReselectSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = Tree[0];

        // The user selects the repo in the tree (loads its contents). Set the selection directly rather than
        // via the property, so the async-void OnSelectedTreeNodeChanged handler's load can't race the loads
        // below — this leaves the exact state a real first-select produces (repo is the selected node).
#pragma warning disable MVVMTK0034 // deliberately set the backing field to avoid firing the change handler
        _selectedTreeNode = repo;
#pragma warning restore MVVMTK0034
        await LoadFolderContentsAsync(repo.Id, repo.Links);
        var sub = Items.FirstOrDefault(n => !n.HasVersions);
        if (sub is null)
        {
            // Other tests sharing the demo tenant may have removed the seeded subfolder; create one so this
            // self-test doesn't depend on test ordering.
            await _api!.Documents.CreateFolderAsync(repo.Href("children"), "tree-select-" + Guid.NewGuid().ToString("N")[..8]);
            await LoadFolderContentsAsync(repo.Id, repo.Links);
            sub = Items.First(n => !n.HasVersions);
        }

        // …then drills into the subfolder via the CONTENTS list — the tree's selection stays on the repo.
        await LoadFolderContentsAsync(sub.Id, sub.Links);
        var afterDrill = _currentFolderId!.Value;

        // Re-tap the still-selected repo node in the tree: the fix reloads the list back to the repo.
        await ReselectTreeFolderAsync(repo);
        return (afterDrill, _currentFolderId!.Value, Items.Select(n => n.Name).ToArray());
    }

    // Search-hit reveal-in-tree (issue #340): activating a document search hit expands + selects its parent folder
    // in the tree, loads that folder into the list, and selects the document there. Seeds a nested doc so the reveal
    // has a real ancestor chain (repo → subfolder → doc), collapses the tree + moves the list away, then drives the
    // real OpenSearchResultAsync and reports whether the tree, list, and list-selection all landed on the target.
    internal async Task<(bool TreeSelectedParent, bool ListHasDoc, bool ListSelectedDoc)> SearchRevealSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = Tree[0];

        // Seed a subfolder + a document inside it (independent of test ordering).
        var subName = "reveal-" + Guid.NewGuid().ToString("N")[..8];
        await _api!.Documents.CreateFolderAsync(repo.Href("children"), subName);
        await LoadFolderContentsAsync(repo.Id, repo.Links);
        var sub = Items.First(n => n.IsFolder && !n.IsReference && n.Name == subName);
        var docName = "reveal-doc-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var docId = await _api.Documents.UploadFileAsync(sub.Href("children"), docName, System.Text.Encoding.UTF8.GetBytes("reveal me"));

        // Start from a clean slate: nothing selected in the tree, the list showing the repo root (not the subfolder).
        await LoadFolderContentsAsync(repo.Id, repo.Links);
#pragma warning disable MVVMTK0034 // set the backing field so the reveal's selection is a real change, not a no-op
        _selectedTreeNode = null;
#pragma warning restore MVVMTK0034

        // Activate the search hit for the seeded document — the real path a double-click drives. A real hit
        // carries its advertised addresses (#443), so the synthetic one does too, built from the rows in hand.
        var docRow = (await _api.Documents.GetChildrenAsync(sub.Href("children"))).Single(n => n.Id == docId);
        await OpenSearchResultAsync(new SearchResultViewModel
        {
            Id = docId,
            Name = docName,
            IsFolder = false,
            ParentId = sub.Id,
            Path = "",
            Links = new Dictionary<string, string> { ["self"] = docRow.Href("self"), ["parent"] = sub.Href("self") },
        });

        return (
            SelectedTreeNode?.Id == sub.Id,                               // parent folder revealed + selected in the tree
            Items.Any(n => n.Id == docId && !n.IsReference),             // the document is listed in the list pane
            SelectedItem?.Id == docId);                                  // …and selected there
    }

    // The references dialog's "Open" must open the chosen folder AND select the item for viewing — its real row in
    // the primary location, and its reference (shortcut) row in a referencing folder (that reference row was
    // previously skipped because the selection filtered out references). Drives OpenFolderAsync exactly as the
    // dialog's Open path does.
    internal async Task<(bool SelectedInPrimary, bool SelectedReferenceInRefFolder)> OpenReferenceSelectsDocumentSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = Tree[0];

        // A document filed at the repo root (its primary location) and a subfolder that references it.
        var refFolderName = "refopen-" + Guid.NewGuid().ToString("N")[..8];
        await _api!.Documents.CreateFolderAsync(repo.Href("children"), refFolderName);
        await LoadFolderContentsAsync(repo.Id, repo.Links);
        var refFolder = Items.First(n => n.IsFolder && !n.IsReference && n.Name == refFolderName);
        var docName = "refopen-doc-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var docId = await _api.Documents.UploadFileAsync(repo.Href("children"), docName, System.Text.Encoding.UTF8.GetBytes("body"));
        await _api.Documents.CreateReferenceAsync(refFolder.Href("references"), docId);

        // Open the primary location selecting the doc → its real (non-reference) row is selected.
        await OpenFolderAsync(repo.DocumentSelfHref, docId);
        var primaryOk = SelectedItem is { IsReference: false } primary && primary.Id == docId;

        // Open the referencing folder selecting the doc → its reference (shortcut) row is selected for viewing.
        await OpenFolderAsync(refFolder.DocumentSelfHref, docId);
        var refOk = SelectedItem is { IsReference: true } shortcut && shortcut.Id == docId;

        return (primaryOk, refOk);
    }

    // Repository sort order (issue #339): folders always come first, alphabetically, then documents; the tree's
    // folder children are alphabetical too. Seeds subfolders in NON-alphabetical creation order + a document, then
    // checks the list order and the tree-child order both landed alphabetically-folders-first.
    internal async Task<(bool ListFoldersAlphaThenDoc, bool TreeFoldersAlpha)> RepositorySortSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = Tree[0];

        var parentName = "sort-" + Guid.NewGuid().ToString("N")[..8];
        await _api!.Documents.CreateFolderAsync(repo.Href("children"), parentName);
        await LoadFolderContentsAsync(repo.Id, repo.Links);
        var parent = Items.First(n => n.IsFolder && !n.IsReference && n.Name == parentName);

        // Subfolders created out of alphabetical order + a document filed alongside them.
        await _api.Documents.CreateFolderAsync(parent.Href("children"), "Zebra");
        await _api.Documents.CreateFolderAsync(parent.Href("children"), "Apple");
        await _api.Documents.CreateFolderAsync(parent.Href("children"), "Mango");
        await _api.Documents.UploadFileAsync(parent.Href("children"), "a-document.txt", System.Text.Encoding.UTF8.GetBytes("doc"));

        // List: folders first (alphabetical), then the document — regardless of creation order.
        await LoadFolderContentsAsync(parent.Id, parent.Links);
        var listOk = Items.Select(n => n.Name).SequenceEqual(["Apple", "Mango", "Zebra", "a-document"]);

        // Tree: expand the parent; its folder children are alphabetical (the document isn't a tree node).
        await repo.EnsureExpandedAsync();
        var parentNode = repo.Children.First(n => n.Id == parent.Id);
        await parentNode.EnsureExpandedAsync();
        var treeOk = parentNode.Children.Where(n => !n.IsLauncher && !n.IsSynthetic)
            .Select(n => n.Name).SequenceEqual(["Apple", "Mango", "Zebra"]);

        return (listOk, treeOk);
    }

    // Navigating to a different folder clears the panes right of the list (index-data / preview / comments) so a
    // freshly-selected folder doesn't keep showing the previously-viewed document — parity with the web client
    // (ADR 0516). A same-folder reload keeps the current detail. Driven against the running Api; DetailTitle stands
    // in for the populated detail panes (what a real selection sets and what ClearDetail resets).
    internal async Task<(bool ClearedOnFolderChange, bool KeptOnSameFolderReload)> FolderChangeResetsPanesSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = Tree[0];
        await LoadFolderContentsAsync(repo.Id, repo.Links);

        // A guaranteed different folder to navigate into — create one if the shared demo tenant has none.
        var sub = Items.FirstOrDefault(n => n.IsFolder && !n.IsReference);
        if (sub is null)
        {
            await _api!.Documents.CreateFolderAsync(repo.Href("children"), "panereset-" + Guid.NewGuid().ToString("N")[..8]);
            await LoadFolderContentsAsync(repo.Id, repo.Links);
            sub = Items.First(n => n.IsFolder && !n.IsReference);
        }

        // Populate the detail (as selecting a document does), then navigate to a DIFFERENT folder — must clear.
        DetailTitle = "sentinel-A";
        await LoadFolderContentsAsync(sub.Id, sub.Links);
        var clearedOnFolderChange = string.IsNullOrEmpty(DetailTitle);

        // A same-folder reload (e.g. after an in-place operation) must KEEP the current detail.
        DetailTitle = "sentinel-B";
        await LoadFolderContentsAsync(sub.Id, sub.Links);
        var keptOnSameFolderReload = DetailTitle == "sentinel-B";

        return (clearedOnFolderChange, keptOnSameFolderReload);
    }

    // Exercises the tree-pane context menu's Manage-access action (ADR "Tree-pane context menu with
    // manage-access") on the node kinds only the TREE exposes: a repository ROOT (never a contents-list row, so
    // the list-row menu could never reach it) and a nested subfolder. Returns the ACL round-trip result for each,
    // proving a tree node's Id is a valid ACL target the way OnTreeManageAccess uses it.
    internal async Task<(bool RootGranted, bool SubfolderGranted)> TreeManageAccessSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var root = Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "treeacl-" + suffix;
        await CreateSubfolderAsync(root.Id, root.Href("children"), name);
        await root.ReloadChildrenAsync();
        var sub = root.Children.First(c => c.Name == name);

        var granteeId = await _api!.Admin.CreateUserAsync($"treeacl-{suffix}@simplarchive.local", $"TreeAcl {suffix}");
        var viewer = new AclRights(
            CanSee: true, CanReadContent: true, CanEditContent: false, CanEditIndexData: false,
            CanCreateSubItems: false, CanDelete: false, CanMove: false, CanAnnotate: false, CanManagePermissions: false);

        var rootGranted = await GrantAndRevokeAsync(root.DocumentSelfHref, granteeId.Id, viewer);
        var subGranted = await GrantAndRevokeAsync(sub.DocumentSelfHref, granteeId.Id, viewer);

        await DeleteFolderAsync(sub.Id, sub.Href("self")); // clean up
        return (rootGranted, subGranted);
    }

    // Exercises the tree context menu's folder actions that act on the RIGHT-CLICKED node rather than the
    // contents-list selection (ADR "Tree-pane context menu"): move a tree folder under another folder, and place
    // a reference (shortcut) to it elsewhere. Returns whether each landed where it should.
    internal async Task<(bool Moved, bool Referenced)> TreeFolderMoveAndReferenceSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var root = Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var subjectName = $"treemove-{suffix}";
        var destinationName = $"treedest-{suffix}";
        await CreateSubfolderAsync(root.Id, root.Href("children"), subjectName);
        await CreateSubfolderAsync(root.Id, root.Href("children"), destinationName);
        var children = await _api!.Documents.GetChildrenAsync(root.Href("children"));
        var subject = children.First(c => c.Name == subjectName);
        var destination = children.First(c => c.Name == destinationName);

        await MoveFolderAsync(subject.Href("self"), subject.Name, destination.Id);
        var moved = (await _api.Documents.GetChildrenAsync(destination.Href("children"))).Any(c => c.Id == subject.Id);

        // Place a reference to the moved folder back under the repository root.
        await PlaceReferenceAsync(subject.Id, subject.Name, root.Href("references"));
        var referenced = (await _api.Documents.GetReferencesAsync(root.Href("references"))).Any(r => r.TargetId == subject.Id);

        await DeleteFolderAsync(destination.Id, destination.Href("self")); // clean up (takes the subject with it)
        return (moved, referenced);
    }

    // Exercises the empty-folder tree icon (ADR "Empty-folder tree icon", issue #352) against the real Api: a
    // freshly-created folder is empty; the same folder holding only a DOCUMENT is not (the distinction the flag
    // must not get wrong, since a documents-only folder is still a leaf in the folders-only tree).
    internal async Task<(bool EmptyWhenNew, bool NotEmptyWithADocument)> EmptyFolderIconSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var root = Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });

        var name = "treeempty-" + Guid.NewGuid().ToString("N")[..8];
        await CreateSubfolderAsync(root.Id, root.Href("children"), name);
        await root.ReloadChildrenAsync();
        var emptyWhenNew = root.Children.First(c => c.Name == name).IsEmptyFolder;

        var child = root.Children.First(c => c.Name == name);
        await _api!.Documents.UploadFileAsync(child.Href("children"), "a-document.txt", System.Text.Encoding.UTF8.GetBytes("doc"));
        await root.ReloadChildrenAsync();
        var notEmptyWithADocument = !root.Children.First(c => c.Name == name).IsEmptyFolder;

        await DeleteFolderAsync(child.Id, child.Href("self")); // clean up
        return (emptyWhenNew, notEmptyWithADocument);
    }

    private async Task<bool> GrantAndRevokeAsync(string documentSelfHref, Guid granteeId, AclRights rights)
    {
        // Grant through the principal row the ACL view offers, then revoke through the entry row that grant
        // produced — the same path the dialog takes, which is the point of a self-test (ADR 0555).
        var before = await _api!.Documents.GetAclAsync(documentSelfHref);
        await _api.Documents.SetAclEntryAsync(before.Principals.Single(p => p.Type == "users" && p.Id == granteeId), rights);

        var after = await _api.Documents.GetAclAsync(documentSelfHref);
        var entry = after.Entries.FirstOrDefault(e => e.PrincipalType == "users" && e.PrincipalId == granteeId);
        var granted = entry is { Rights.CanSee: true };
        if (entry is not null)
        {
            await _api.Documents.RevokeAclEntryAsync(entry);
        }

        return granted;
    }

    // Exercises the tree-pane folder context-menu actions (ADR "Desktop tree-pane folder context menu") end to
    // end against the running Api: create a subfolder under a repository, rename it, delete it.
    internal async Task<(bool Created, bool Renamed, bool Deleted)> TreeFolderActionsSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = Tree[0];

        var name = "treeact-" + Guid.NewGuid().ToString("N")[..8];
        await CreateSubfolderAsync(repo.Id, repo.Href("children"), name);
        var created = (await _api!.Documents.GetChildrenAsync(repo.Href("children"))).FirstOrDefault(c => c.Name == name);
        if (created is null)
        {
            return (false, false, false);
        }

        var renamed = name + "-r";
        await RenameFolderAsync(created.Href("self"), renamed);
        var isRenamed = (await _api!.Documents.GetChildrenAsync(repo.Href("children"))).Any(c => c.Id == created.Id && c.Name == renamed);

        await DeleteFolderAsync(created.Id, created.Href("self"));
        var isDeleted = (await _api!.Documents.GetChildrenAsync(repo.Href("children"))).All(c => c.Id != created.Id);

        return (true, isRenamed, isDeleted);
    }

    // Creating a subfolder must NOT collapse the tree — the parent folder (whose contents are shown) stays in the
    // tree, expanded, now showing the new child (ADR "Keep the desktop tree expanded on a structural change", see
    // DesktopTreeFolderActionsTests). Reference-equality on the parent node distinguishes the targeted reload from
    // the old full rebuild (which replaced every node).
    internal async Task<bool> NewFolderKeepsTreeExpandedSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = Tree[0];
        await repo.ReloadChildrenAsync(); // materialise + expand the repository node, as navigating into it would

        var name = "treeexp-" + Guid.NewGuid().ToString("N")[..8];
        await CreateSubfolderAsync(repo.Id, repo.Href("children"), name);

        var stillExpanded = Tree.Contains(repo) && repo.IsExpanded && repo.Children.Any(c => c.Name == name);

        var created = (await _api!.Documents.GetChildrenAsync(repo.Href("children"))).FirstOrDefault(c => c.Name == name);
        if (created is not null)
        {
            await DeleteFolderAsync(created.Id, created.Href("self"));
        }
        return stillExpanded;
    }

    // Per-folder contents sort order (ADR "Per-folder contents sort order", see DesktopFolderContentsSortTests):
    // a fresh folder defaults to DocumentDate; the detail-pane Save round-trips the choice to the server and
    // updates the VM state.
    internal async Task<bool> FolderContentsSortSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = Tree[0];

        var name = "fsort-" + Guid.NewGuid().ToString("N")[..8];
        await _api!.Documents.CreateFolderAsync(repo.Href("children"), name);
        var folder = (await _api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == name);

        await OpenFolderAsync(folder.Href("self"));
        var defaultIsDocDate = _folderSortOrder == 1 && FolderSortText == Strings.Get("FolderSortDocDate");

        // Through the ONE detail edit now (issue #408): the order is a field in the pane, committed by the same
        // Save as the mask, rather than by a toggle of its own.
        await LoadDetailAsync(new NodeViewModel { Id = folder.Id, Name = name, HasChildren = false, HasVersions = false, Links = folder.Links });
        await BeginEditCommand.ExecuteAsync(null);
        EditSortOrder = 2; // Created
        await SaveDetailCommand.ExecuteAsync(null);
        var persisted = await _api.Documents.GetContentsSortOrderAsync(folder.Href("children")) == 2;
        var reflected = _detailSortOrder == 2 && !IsEditing && DetailSortText == Strings.Get("FolderSortCreated");

        await DeleteFolderAsync(folder.Id, folder.Href("self"));
        return defaultIsDocDate && persisted && reflected;
    }

    // Headless exercise of the intray drop-zone upload (ADR "Inbox file-list drop-zone", see DesktopIntrayDropTests):
    // uploading dropped bytes puts a new item in the server intray. Cleans up so the shared demo intray stays tidy.
    internal async Task<bool> IntrayDropSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var name = "intraydrop-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        await UploadFilesToIntrayAsync(new[] { (name, System.Text.Encoding.UTF8.GetBytes("dropped into the intray")) });
        var present = ServerIntray.Any(i => i.Name == name);
        if (ServerIntray.FirstOrDefault(i => i.Name == name) is { } uploaded)
        {
            await _api!.Intray.DeleteIntrayItemAsync(uploaded.Item!);
        }

        return present;
    }

    // Headless exercise of intray send + admin triage (ADR 0532, see DesktopIntraySendTests): the admin uploads an
    // own item, hands it to a freshly-created user via the send-target list, and — as a CanManageIntrayes holder —
    // sees it in that user's intray via ?user=. Cleans up the item + the user so the shared demo stays tidy.
    internal async Task<bool> IntraySendSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        CanManageIntrayes = (await _api!.GetWhoAmIAsync()).CanManageIntrayes;

        var recipient = await _api.Admin.CreateUserAsync($"send-{Guid.NewGuid():N}@e2e.local", "Send Recipient");

        var name = "send-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        await UploadFilesToIntrayAsync(new[] { (name, System.Text.Encoding.UTF8.GetBytes("hand-off")) });
        if (ServerIntray.FirstOrDefault(i => i.Name == name) is not { } item)
        {
            return false;
        }

        var target = (await IntrayActions.GetSendTargetsAsync()).FirstOrDefault(t => !t.IsGroup && t.Id == recipient.Id);
        if (target is null)
        {
            return false;
        }

        await IntrayActions.SendAsync(item, target);
        var leftOwnIntray = ServerIntray.All(i => i.Name != name);                                  // gone from mine
        var inRecipientIntray = (await _api.Intray.ListAsync(user: recipient.Id)).Items.Any(i => i.Name == name); // now theirs

        if ((await _api.Intray.ListAsync(user: recipient.Id)).Items.FirstOrDefault(i => i.Name == name) is { } handedOver)
        {
            await _api.Intray.DeleteIntrayItemAsync(handedOver);
        }

        await _api.Admin.DeleteUserAsync(recipient);
        return leftOwnIntray && inRecipientIntray;
    }

    // Headless exercise of the Personal-space grouping (ADR "GUI-tree Personal space grouping", see
    // DesktopPersonalSpaceTreeTests): the Personal node nests the Intray + Check-out launcher nodes above its real
    // subfolders, and selecting a launcher switches to the matching bottom tab.
    internal async Task<List<string>> PersonalLaunchersSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var log = new List<string>();

        await LoadRootAsync();
        var personal = Tree.FirstOrDefault(n => n.IsPersonal);
        if (personal is null)
        {
            log.Add("FAILED: no Personal node.");
            return log;
        }

        var children = (await LoadPersonalChildrenAsync(new TreeNodeViewModel(personal.Id, personal.Name, false, null, links: personal.Links))).ToList();
        log.Add(children is [{ PersonalKind: "intray", IsLauncher: true, LauncherTab: 1, IconValue: "mdi-inbox-arrow-down" },
        { PersonalKind: "checkout", IsLauncher: true, LauncherTab: 2, IconValue: "mdi-lock-open-variant-outline" }, ..]
            ? "OK: Intray + Check-out launchers nested first under Personal."
            : "FAILED: launcher nodes missing or out of order.");

        // Selecting the Intray launcher switches to the Intray bottom tab (index 1); the tab index is set
        // synchronously in the launcher branch before any await.
        SelectedTreeNode = children[0];
        log.Add(SelectedTab == 1 ? "OK: selecting the Intray launcher switched to tab 1." : $"FAILED: tab is {SelectedTab}.");

        SelectedTreeNode = children[1];
        log.Add(SelectedTab == 2 ? "OK: selecting the Check-out launcher switched to tab 2." : $"FAILED: tab is {SelectedTab}.");
        return log;
    }

    internal void SetPreviewPagesForScreenshot(IEnumerable<Bitmap> pages) => Preview.SetPreviewPagesForScreenshot(pages);

    internal void SetPreviewNotesForScreenshot(IReadOnlyList<NoteBox> notes) => Preview.SetScreenshotNotesOnFirstPage(notes);

    // Seeds a preview page with a bitmap + hit-overlay words and a find query, for the headless overlay
    // screenshot (no network).
    internal void PopulateHitOverlayForScreenshot(Bitmap image, IReadOnlyList<VersionsClient.TextLayoutBox> words, string query)
    {
        var page = new PreviewPageViewModel(image);
        page.SetWords(words);
        // Two sample sticky-note boxes (ADR "Post-it note boxes") — the second Selected — so the screenshot shows
        // the always-visible sized note rendering plus the multi-select outline (ADR "Annotation multi-select").
        page.Notes =
        [
            new NoteBox(Guid.NewGuid(), 0, 0.52, 0.12, 0.30, 0.10, "#FFEB3B", CanEdit: true, "A resizable sticky note that always shows its text."),
            new NoteBox(Guid.NewGuid(), 0, 0.52, 0.30, 0.30, 0.10, "#B3E5FC", CanEdit: true, "A second, selected note.", Selected: true),
        ];
        Preview.SetHitOverlayPageForScreenshot(page, query);
    }

    // ---- Audit log viewer (ADR "Desktop audit viewer") -----------------------------------------------

    // Gates the Audit tab (set from whoami on login); true for any User holding CanViewAuditLog.
    [ObservableProperty] private bool _canViewAuditLog;

    public ObservableCollection<AuditEventRowViewModel> AuditEvents { get; } = [];

    [ObservableProperty] private string _auditActionFilter = "";
    [ObservableProperty] private DateTimeOffset? _auditFrom;
    [ObservableProperty] private DateTimeOffset? _auditTo;
    [ObservableProperty] private bool _auditHasMore;
    [ObservableProperty] private bool _auditBusy;
    [ObservableProperty] private string _auditVerifyStatus = "";
    [ObservableProperty] private bool _auditVerifyValid;
    [ObservableProperty] private bool _auditVerifyShown;
    // WORM-segment verification (ADR "Audit WORM segment verify").
    [ObservableProperty] private string _wormVerifyStatus = "";
    [ObservableProperty] private bool _wormVerifyValid;
    [ObservableProperty] private bool _wormVerifyShown;
    [ObservableProperty] private int _auditRetentionDays = 365;
    [ObservableProperty] private string _auditRetentionNote = "";
    private string? _auditNextCursor;

    [RelayCommand]
    private Task RunAuditSearch() => LoadAuditPageAsync(reset: true);

    [RelayCommand]
    private async Task ClearAuditFilters()
    {
        AuditActionFilter = "";
        AuditFrom = null;
        AuditTo = null;
        await LoadAuditPageAsync(reset: true);
    }

    [RelayCommand]
    private Task LoadMoreAudit() => LoadAuditPageAsync(reset: false);

    private async Task LoadAuditPageAsync(bool reset)
    {
        if (_api is null)
        {
            return;
        }

        AuditBusy = true;
        try
        {
            if (reset)
            {
                AuditEvents.Clear();
                _auditNextCursor = null;
                AuditVerifyShown = false;
            }

            // "To" is inclusive of the whole selected day.
            var to = AuditTo is { } t ? new DateTimeOffset(t.Date.AddDays(1), TimeSpan.Zero) : (DateTimeOffset?)null;
            var page = await _api.Audit.GetAuditEventsAsync(
                string.IsNullOrWhiteSpace(AuditActionFilter) ? null : AuditActionFilter,
                AuditFrom,
                to,
                _auditNextCursor);

            foreach (var e in page.Events)
            {
                AuditEvents.Add(new AuditEventRowViewModel
                {
                    Timestamp = e.Timestamp,
                    ActorName = e.ActorName,
                    ActorType = e.ActorType,
                    Action = e.Action,
                    TargetType = e.TargetType,
                    TargetName = e.TargetName,
                    Details = e.Details,
                });
            }

            _auditNextCursor = page.NextCursor;
            AuditHasMore = _auditNextCursor is not null;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoadAudit"), e.Message);
        }
        finally
        {
            AuditBusy = false;
        }
    }

    // Fetches the NDJSON export bytes for the current filters (the code-behind saves them to a chosen file).
    public Task<byte[]>? ExportAuditBytesAsync()
    {
        if (_api is null)
        {
            return null;
        }

        var to = AuditTo is { } t ? new DateTimeOffset(t.Date.AddDays(1), TimeSpan.Zero) : (DateTimeOffset?)null;
        return _api.Audit.ExportAuditEventsAsync(string.IsNullOrWhiteSpace(AuditActionFilter) ? null : AuditActionFilter, AuditFrom, to);
    }

    [RelayCommand]
    private async Task VerifyAudit()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var result = await _api.Audit.VerifyAuditChainAsync();
            AuditVerifyValid = result.Valid;
            AuditVerifyStatus = result.Valid
                ? $"Chain intact ({result.CheckedCount} events)"
                : $"Tampering detected at #{result.BrokenAtSequence}";
            AuditVerifyShown = true;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrVerifyAudit"), e.Message);
        }
    }

    [RelayCommand]
    private async Task VerifyWorm()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var result = await _api.Audit.VerifyAuditWormAsync();
            WormVerifyValid = result.Valid;
            WormVerifyStatus = result.Valid
                ? $"WORM sealed intact ({result.CheckedCount} events, {result.SegmentCount} segments)"
                : $"WORM {result.Reason} at #{result.BrokenAtSequence}";
            WormVerifyShown = true;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrVerifyWorm"), e.Message);
        }
    }

    private async Task LoadRetentionAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var retention = await _api.Audit.GetAuditRetentionAsync();
            AuditRetentionDays = retention.RetentionDays;
            AuditRetentionNote = retention.ChainStartSequence > 0
                ? $"Retained from #{retention.ChainStartSequence}" + (retention.LastPurgedAt is { } lp ? $" · last purged {lp.LocalDateTime:yyyy-MM-dd}" : "")
                : "";
        }
        catch (Exception)
        {
            // leave defaults
        }
    }

    [RelayCommand]
    private async Task SaveRetention()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var retention = await _api.Audit.SetAuditRetentionAsync(AuditRetentionDays);
            AuditRetentionDays = retention.RetentionDays;
            Status = Strings.Get("StAuditRetUpdated");
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }

    // Purges aged audit events for the tenant (the code-behind confirms first). Returns the count purged.
    public async Task<int> PurgeAuditAsync()
    {
        if (_api is null)
        {
            return 0;
        }

        try
        {
            var result = await _api.Audit.PurgeAuditAsync();
            await LoadRetentionAsync();
            await LoadAuditPageAsync(reset: true);
            Status = string.Format(Strings.Get("StPurgedAudit"), result.PurgedCount);
            return result.PurgedCount;
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
            return 0;
        }
    }
}
