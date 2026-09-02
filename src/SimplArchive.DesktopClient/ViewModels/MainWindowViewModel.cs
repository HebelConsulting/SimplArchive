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
using SimplArchive.Presentation;
using SimplArchive.Theming;

namespace SimplArchive.DesktopClient.ViewModels;

// The desktop workbench — mirrors the web Repositories tab: bottom tabs, a ribbon, and the
// tree │ contents-list │ (index-data over (preview │ chat)) panes. See ADR "Desktop workbench UI".
public sealed partial class MainWindowViewModel : ObservableObject, IShellContext
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
    [ObservableProperty] private string _userEmail = string.Empty;
    [ObservableProperty] private string _status = "Not logged in.";

    // The bottom TabControl's selected index (0 = Repositories) — bound so opening a search result can switch
    // back to the workbench.
    [ObservableProperty] private int _selectedTab;

    // The bottom tabs (Repositories/Intray/Tasks/Search) are a TabControl in the view; only Repositories has
    // content in this slice.

    // The Recycle bin tab (ADR "Desktop recycle bin parity") — its own master-detail VM with an INDEPENDENT
    // preview (RecycleBin.Preview), so a deleted document's preview never entangles the Repositories/Intray one.
    public RecycleBinTabViewModel RecycleBin { get; }

    // The Search tab's own state (#517 tranche 2) — see SearchTabViewModel for what moved and why.
    public SearchTabViewModel Search { get; }

    // The Check-out tab (ADR "Document check-out / check-in") — the caller's checked-out documents + their
    // local working-copy status.
    public CheckoutTabViewModel Checkout { get; }

    // The Contacts and Calendar tabs (#564) — the caller's addressbooks and calendars. Their own VMs, like
    // Check-out above: a tab's worth of state belongs to the tab, and this file is far over the 1000-line
    // ceiling. Treated as ONE surface in review (ADR 0511), so they are declared together.
    public ContactsTabViewModel ContactsTab { get; }

    public CalendarTabViewModel CalendarTab { get; }

    // The environment strip (#501) — set from the chosen server profile at login, empty for the normal case.
    public EnvironmentBannerViewModel EnvBanner { get; } = new();

    public MainWindowViewModel()
    {
        // Everything the window owns is constructed FIRST: each takes this as its context, and LoadLayout
        // below asks a tab to restore its own pane rows, so a tab built after it would be null (#517).
        Preview = new PreviewViewModel(this);
        Intray = new IntrayTabViewModel(this);
        RecycleBin = new RecycleBinTabViewModel(this);
        Search = new SearchTabViewModel(this);
        Checkout = new CheckoutTabViewModel(this);
        Audit = new AuditTabViewModel(this);
        ContactsTab = new ContactsTabViewModel(this);
        CalendarTab = new CalendarTabViewModel(this);

        LoadLayout();
        WireContentsFilter(); // VisibleItems follows Items through every mutation site (see the partial)
    }


    // Sets the authenticated api client for the whole workbench, including both preview surfaces + the Recycle
    // bin tab (so every surface shares the same session token).
    private void UseApi(SimplArchiveApiClient api)
    {
        _api = api;
        SetPreviewApi(api);
        _ocrLanguages = new OcrLanguageCatalog(api);
        RecycleBin.SetApi(api);
        Search.SetApi(api);
        Search.OpenResultRequested = OpenSearchResultAsync;
        ContactsTab.SetApi(api);
        CalendarTab.SetApi(api);
        Intray.SetApi(api);

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

        Intray.ResetLayout();

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

        TreeWidth = TreeCollapsed ? new GridLength(0) : _treeSaved;
        ListWidth = ListCollapsed ? new GridLength(0) : _listSaved;
        // Auto, not a persisted value: this pane fits its content (ADR 0550), and a stored height would be the
        // height of whatever happened to be selected when it was last dragged. Nothing reads a saved height for
        // this pane any more — the collapse toggle expands to Auto too.
        IndexHeight = IndexCollapsed ? new GridLength(0) : GridLength.Auto;
        ChatWidth = ChatCollapsed ? new GridLength(0) : _chatSaved;

        Intray.LoadLayout(settings);

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
        LayoutSettingsStore.Save(settings);
    }

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    // ---- Intray tab: four collapsible/resizable panes (ADR "Collapsible inbox panes") ------------------
    // Same mechanism as the Repositories panes above — each pane's body row height is two-way bound, collapse
    // sets it to 0, and a header caret toggles it. Persisted in the same LayoutSettings.










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
    [ObservableProperty] private string _detailTitle = string.Empty;
    [ObservableProperty] private string _maskLine = string.Empty;

    // System fields — always shown, separate from the mask (ADR "System fields + OCR-language mask field").
    // Name / DocumentDate / OCR languages are read-write; Created / CreatedBy / File extension are read-only.
    // Every read-write field is only editable while the whole pane is in edit mode (ADR "Single pane-level
    // edit toggle on the detail pane"). OCR languages shows only for a TIFF-sourced document.
    [ObservableProperty] private string _sysName = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SysDocumentDateText))] private DateTime? _sysDocumentDate;
    [ObservableProperty] private string _sysCreated = string.Empty;
    [ObservableProperty] private string _sysCreatedBy = string.Empty;
    [ObservableProperty] private string _sysFileExtension = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanEditOcr))] private bool _sysHasTiff;
    [ObservableProperty] private string _sysOcrLanguages = string.Empty;
    // The document's current (latest confirmed) version number — the last line of the detail pane (ADR "Mask-pane
    // current-version line"). Empty for a folder / a document with no confirmed version.
    [ObservableProperty] private string _sysCurrentVersion = string.Empty;

    // Sensitivity label (ADR "Configurable sensitivity labels + upload defaults"): the current per-tenant label
    // (read-only display: name + colour) + the staged edit value bound to the ComboBox (a picker item).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasSensitivity))][NotifyPropertyChangedFor(nameof(DetailSensitivityText))][NotifyPropertyChangedFor(nameof(DetailSensitivityBrush))] private Guid? _detailSensitivityId;
    private string _detailSensitivityName = string.Empty;
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

    // The tenant-administration dialogs' view-models; null when not signed in. The labels' caller reloads the
    // catalog on close (it feeds a picker); the domains' does not, because nothing caches them (#667).
    public SensitivityLabelsViewModel? CreateSensitivityLabelsViewModel() => _api is { } api ? new SensitivityLabelsViewModel(api) : null;
    public MailDomainsViewModel? CreateMailDomainsViewModel() => _api is { } api ? new MailDomainsViewModel(api) : null;

    // Free-form tags (ADR "Document tags"): the selected document's tags (read-only chips), the edit working
    // copy (chips with a remove + an add box over the tenant catalog), and the pending new-tag value.
    public ObservableCollection<string> DetailTags { get; } = [];
    public ObservableCollection<string> EditTags { get; } = [];
    public ObservableCollection<string> TagCatalog { get; } = [];
    [ObservableProperty] private bool _hasDetailTags;
    [ObservableProperty] private string _newTag = string.Empty;
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
    // The OCR language catalogue, shared with the tenant and detail panes (#517). A service because nothing
    // binds to it and it was three copies of the same lazy-load; see OcrLanguageCatalog.
    private OcrLanguageCatalog? _ocrLanguages;

    // The Repositories + Intray preview surface (state + render + find + hit-overlay + full-screen). Extracted to
    // its own PreviewViewModel so the Recycle bin tab can own a SEPARATE instance (RecycleBin.Preview) and the
    // two previews are never entangled — see ADR "Desktop recycle bin parity". Bound by the PreviewPane control.
    public PreviewViewModel Preview { get; }


    // The Search tab's own preview instance, for the same reason as the Intray's (#462): a preview shown while
    // browsing search results must not leak into the Repositories tab, and vice versa.

    // Leaves full-screen for ALL preview surfaces (the Esc key binding + a tab switch) — only the active tab's
    // preview can actually be full-screen, so clearing all is safe.
    [RelayCommand]
    private void ExitPreviewFullscreen()
    {
        Preview.ExitFullscreen();
        Intray.Preview.ExitFullscreen();
        RecycleBin.Preview.ExitFullscreen();
    }

    [ObservableProperty] private string _newComment = string.Empty;

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
            TokenSessions.Current.Record(DesktopClientOptions.ApiBaseUrl, result);
            UseApi(new SimplArchiveApiClient(result.AccessToken));
            UserEmail = result.Email ?? "(unknown)";
            IsLoggedIn = true;
            await SetupUserContextAsync();
            await LoadRootAsync();

            // A scheme-launched deep link (#761) parked by Program.Main waits for exactly this moment: the
            // workbench is loaded and the tree exists to reveal into.
            if (PendingDeepLink is { } pending)
            {
                PendingDeepLink = null;
                await GoToDeepLinkAsync(pending);
            }
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
        SetPreviewApi(null);
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
        UserEmail = string.Empty;
        UserDisplayName = string.Empty;

        // Reset the right-gated tabs so the next user's rights apply cleanly.
        IsTenantAdmin = false;
        CanManageUsers = false;
        CanManageServiceAccounts = false;
        CanImpersonate = false;
        IsImpersonating = false;
        ImpersonatedName = null;
        _adminApi = null;
        CanViewAuditLog = false;
        Audit.Reset();
        CanLegalHold = false;
        CanManageClassification = false;
        CanOverrideCheckout = false;
        HasExportRight = false;
        HasImportRight = false;
        TenantSettingsLoaded = false;
        TenantEditingGroup = null;
        Notifications.Clear();
        UnreadNotificationCount = 0;
        Search.SavedSearches.Clear();
        Search.LastSearchQueryString = string.Empty;
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
    private readonly TreeExpansionMemory _treeMemory = new();

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

        // The user's personal repository pinned above the shared ones, which are alphabetical (issue #339).
        // Composed by the SHARED rule (ADR 0689) rather than spelled out here, because the target pickers must
        // offer exactly these roots and were building their own list from GET /repositories alone — which
        // excludes the personal space, so it silently was not offerable.
        var personal = await _api.Profile.GetPersonalRepositoryAsync();
        foreach (var root in SimplArchive.Presentation.FilingRoots.Compose(personal, repositories, r => r.Name))
        {
            var repository = root.Node;
            // The personal space is always expandable — it holds at least the Intray + Check-out launcher nodes
            // (ADR "GUI-tree Personal space grouping"), even before any real subfolder exists — and its children
            // load through the loader that adds them.
            Tree.Add(root.Selectable
                ? new TreeNodeViewModel(repository.Id, repository.Name, repository.HasSubfolders, LoadTreeChildrenAsync, links: repository.Links, hasReferences: repository.HasReferences, hasChildren: repository.HasChildren, admits: repository.Admits, icon: repository.Icon,
                    canDelete: repository.CanDelete, canEditIndexData: repository.CanEditIndexData, canMove: repository.CanMove, canManagePermissions: repository.CanManagePermissions, canCreateChildren: repository.CanCreateChildren)
                : new TreeNodeViewModel(repository.Id, repository.Name, hasSubfolders: true, LoadPersonalChildrenAsync, links: repository.Links, isPersonal: true));
        }

        // Tenant admins get a synthetic "Administration → Users" branch (ADR "Tenant-admin Administration → Users
        // view") to browse every user's personal space; its children load from the admin endpoint.
        if (IsTenantAdmin)
        {
            Tree.Add(new TreeNodeViewModel(Guid.Empty, "Administration", true, LoadAdminRootAsync, syntheticIcon: "mdi-shield-account"));
        }

        // The roots are the only nodes anyone constructs directly; every descendant inherits the callback as it
        // loads, so a new place that creates child nodes cannot forget to wire it.
        // Remembering the tree's shape is its own responsibility and lives in its own class (#687-adjacent
        // size rule): this view-model only says which context it is in and hands over the roots.
        _treeMemory.Use(DesktopClientOptions.ApiBaseUrl, UserEmail);
        foreach (var root in Tree)
        {
            root.ExpansionChanged = _treeMemory.Record;
        }

        await _treeMemory.RestoreAsync(Tree);
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
            hasChildren: r.HasChildren,
            // Carries `take-over` when this caller may perform it (ADR 0672) — absent otherwise, so the menu
            // item is simply not drawn rather than offering a button that answers 403.
            links: r.Links,
            canDelete: r.CanDelete, canEditIndexData: r.CanEditIndexData, canMove: r.CanMove, canManagePermissions: r.CanManagePermissions, canCreateChildren: r.CanCreateChildren));
    }

    // The Personal repository nests the Intray + Check-out launcher nodes above its real subfolders, mirroring
    // /SimplArchive/Personal (ADR "GUI-tree Personal space grouping"). Selecting a launcher switches to the matching
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
            .Select(c => new TreeNodeViewModel(c.Id, c.Name, c.HasSubfolders, LoadTreeChildrenAsync, links: c.Links, hasReferences: c.HasReferences, hasChildren: c.HasChildren, admits: c.Admits, icon: c.Icon,
                canDelete: c.CanDelete, canEditIndexData: c.CanEditIndexData, canMove: c.CanMove, canManagePermissions: c.CanManagePermissions, canCreateChildren: c.CanCreateChildren));

        // Shortcuts, or none where the folder advertises none — see TreeReferenceNodes for why that is not the
        // same question as `children` above, and for the crash it stopped being (#735).
        var referenceNodes = await TreeReferenceNodes.ForAsync(node, _api.References, LoadTreeChildrenAsync);

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
        // Cleared here, decided below once the folder's links are in hand: the `folders` rel is what says whether
        // this folder takes a subfolder (#634). Leaving the PREVIOUS folder's answer standing across the load is
        // the ADR 0559 mistake — the button stays clickable throughout it.
        CanCreateFolder = false;
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
            (var children, var sortOrder, CanCreateFolder) = await _api.Documents.GetFolderContentsAsync(links["children"]);
            var references = await _api.References.GetReferencesAsync(links["references"]);
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
                    CanDelete = child.CanDelete,
                    CanEditIndexData = child.CanEditIndexData,
                    CanMove = child.CanMove,
                    CanManagePermissions = child.CanManagePermissions,
                    CanCreateChildren = child.CanCreateChildren,
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
                    CreatedBy = child.CreatedBy,
                    SensitivityLabelName = child.SensitivityLabelName,
                    SensitivityLabelColor = child.SensitivityLabelColor,
                    VersionCount = child.VersionCount,
                    VersionCreatedAt = child.VersionCreatedAt,
                    MaskIconToken = child.Icon,
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

                    // The target's columns, so a shortcut row reads like the real row beside it (#768).
                    DocumentType = reference.DocumentType,
                    DocumentDate = reference.DocumentDate,
                    SizeBytes = reference.SizeBytes,
                    Tags = reference.Tags ?? [],
                    CreatedBy = reference.CreatedBy,
                    SensitivityLabelName = reference.SensitivityLabelName,
                    SensitivityLabelColor = reference.SensitivityLabelColor,
                    VersionCount = reference.VersionCount,
                    VersionCreatedAt = reference.VersionCreatedAt,
                    MaskIconToken = reference.Icon,
                });
            }

            ApplyContentSort(); // keep the chosen column sort across folder navigation
            Status = string.Format(Strings.Get("StItems"), Items.Count);

            // Where you are, said in the tree — after the load, because the folder is only definitely open once
            // its contents are.
            await MarkOpenFolderInTreeAsync();

            // With nothing selected, the detail pane describes the folder you are standing in. Without this the
            // pane simply went blank on every folder change, so a folder's own mask and index fields were
            // reachable only by selecting its ROW from the parent — and not at all for a repository root, which
            // has no parent to be listed in. The web has described a selected folder since #408; this is the
            // same subject rule, for the open folder (ADR 0511 keeps the two clients one surface).
            if (SelectedItem is null && !isReload)
            {
                await ShowOpenFolderDetailAsync();
            }
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
    public string OwnerHeader => ColumnHeader("owner", Strings.Get("SortOwner"));
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
        OnPropertyChanged(nameof(OwnerHeader));
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
            "owner" => items.OrderBy(n => n.CreatedBy, StringComparer.OrdinalIgnoreCase),
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
            case 1 when Intray.SelectedServerItem is not null:
                await Intray.OpenServerItemCommand.ExecuteAsync(null);
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
            // The `folders` rel the button is gated on, not `children` (#634): same address, different method,
            // and following the one that enabled the affordance keeps gate and action from drifting.
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
    public Task CreateSubfolderAsync(Guid parentId, string childrenHref, string name, Guid? maskId = null) =>
        CreateChildAsync(parentId, api => api.Documents.CreateFolderAsync(childrenHref, name, maskId), "StCreatedFolder", "StErrCreateFolder", name);

    // A section, and a note, inside a notebook (#564). Both reach the server through an href the row itself
    // advertised — the caller never names a mask, so the rule about what may live where stays on the server.
    public Task CreateSectionAsync(Guid parentId, string sectionsHref, string name) =>
        CreateChildAsync(parentId, api => api.Documents.CreateSectionAsync(sectionsHref, name), "StCreatedSection", "StErrCreateSection", name);

    public Task CreateNoteAsync(Guid parentId, string notesHref, string title, string body) =>
        CreateChildAsync(parentId, api => api.Documents.CreateNoteAsync(notesHref, title, body), "StCreatedNote", "StErrCreateNote", title);

    // A contact, and an appointment, from the tree (#689). The whole filled-in resource goes in one request —
    // nothing exists until the user saves the dialog, so a cancelled one leaves no stub for a DAV client to
    // sync. Their messages name the FOLDER as well as the item, which is why inFolder exists at all: from a
    // tree menu the folder the user aimed at is the only thing distinguishing this from the tab's own create.
    public Task CreateStructuredChildAsync(
        Guid parentId, string createHref, object payload, string okKey, string errKey, string name, string inFolder) =>
        CreateChildAsync(
            parentId, api => api.StructuredEditors.CreateAsync(createHref, payload), okKey, errKey, name, inFolder);

    // The creates differ only in the call and the two strings, so they share one body rather than becoming
    // copies that drift (the fourth would get the fix and the first three would not). What genuinely differs
    // rides in as a lambda at each call site, where a reader wants both the difference and the delegation on
    // one line.
    private async Task CreateChildAsync(
        Guid parentId, Func<SimplArchiveApiClient, Task> create, string okKey, string errKey, string name,
        string? inFolder = null)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await create(_api);
            // Two-argument where the caller named a folder, one where it did not — the message templates differ
            // in arity, and string.Format throws on a template whose placeholders outnumber its arguments.
            Status = inFolder is null
                ? string.Format(Strings.Get(okKey), name)
                : string.Format(Strings.Get(okKey), name, inFolder);
            await ShowNewChildInTreeAsync(parentId);
            if (_currentFolderId == parentId)
            {
                await LoadFolderContentsAsync(parentId);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get(errKey), e.Message); }
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
            await _api.References.CreateReferenceAsync(targetReferencesHref, folderId);
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

                await _api.References.DeleteReferenceAsync(referenceDeleteHref);
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

        try { return await _api.Tags.GetTagCatalogAsync(); } catch (Exception) { return []; }
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
            await _api.References.CreateReferenceAsync(targetReferencesHref, node.Id);
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
            Audit.IsTenantAdmin = me.IsTenantAdmin;
            MfaEnabled = me.MfaEnabled;
            CanResetMfa = me.CanResetMfa;
            CanLegalHold = me.CanLegalHold;
            CanManageClassification = me.CanManageClassification;
            CanOverrideCheckout = me.CanOverrideCheckout;
            CanImpersonate = me.CanImpersonate;
            HasExportRight = me.CanExport;
            HasImportRight = me.CanImport;
            Intray.CanManageIntrays = me.CanManageIntrays;
            CanManageMailRouting = me.CanManageMailRouting;
            IsImpersonating = me.ImpersonatedBy is not null;
            ImpersonatedName = me.ImpersonatedBy is not null ? me.UserName : null;
            _currentUserId = me.UserId;
            UserDisplayName = me.UserName ?? "";
            await LoadMyPhotoAsync();
            if (me.TenantName is { } tenantName && me.UserName is { } userName)
            {
                _localFolders = new LocalFolders(tenantName, userName);
                NativeFileOpener.TempDirectoryOverride = _localFolders.TempDirectory;
                Checkout.SetApi(_api);
                Audit.SetApi(_api);
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


    // Whether the caller may change mail routing (#703) — without it a Mailbox's address field renders
    // read-only instead of offering an edit the server would 403; set from whoami on login.
    [ObservableProperty] private bool _canManageMailRouting;

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


    // ---- Intray view filters (ADR 0532): own-items-only by default; a toggle reveals group intrays, and a
    // CanManageIntrays holder can open a specific user's intray via the picker (mutually exclusive with groups). ----







    // Upload OS files dropped onto the intray file-list straight into the S3-backed intray (ADR "Inbox file-list
    // drop-zone"). The view reads each dropped file into (name, bytes); this uploads them, then refreshes.
    // Drops onto the Personal ▸ Intray / Check-out tree launchers (#467). The work is in DropFiling — this class
    // is already the largest entry on the 1000-line debt list (#466) — and what stays here is what only the
    // view-model can do: own the status line and know what to refresh.
    private DropFiling? _dropFiling;


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


    // "Open in file manager" + "WebDAV settings" (Intray tab) now live in the code-behind (MainWindow.axaml.cs
    // OnOpenWebDavIntray / OnManageWebDav), since opening the settings dialog when WebDAV isn't configured needs
    // the Window (ADR "Desktop inbox WebDAV buttons"). The mount logic stays in Services/OsFileManager.

    // ---- Intray item detail (right panes): a mask/index-data editor + the shared preview -------------------
    // The panes are driven by the focused server item; the mask edits are staged to a `{name}.mask.json`
    // sidecar (ADR "Inbox item classification + preview"). The mask pane is only editable for a server item.

















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
        await Intray.RefreshAsync();
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



    // ---- Refinement panel (ADR "Search-refinement UI", phase 2) ---------------------------------------







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
        Intray.Preview.ExitFullscreen();
        RecycleBin.Preview.ExitFullscreen();

        await (value switch
        {
            0 => RefreshRepositoriesViewAsync(),
            1 => Intray.RefreshAsync(),
            2 => ActivateCheckoutAsync(),
            3 => ActivateSearchAsync(),
            4 => LoadRecycleBinAsync(),
            5 => LoadTasksAsync(),
            6 => LoadPrincipalsAsync(),
            7 => Audit.ActivateAsync(),
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

    private async Task ActivateSearchAsync() => await Search.ActivateAsync();

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



























    // ---- Tag chips (ADR "Document tags") -------------------------------------------------------------
    [RelayCommand]
    private void AddTag()
    {
        var t = NewTag.Trim().ToLowerInvariant();
        if (t.Length is > 0 and <= 100 && !EditTags.Contains(t))
        {
            EditTags.Add(t);
        }

        NewTag = string.Empty;
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

        // The one cross-tab hop the extraction leaves on the shell: switching tab is the shell's job, running
        // the query is the tab's (#517 tranche 2).
        SelectedTab = 3; // Search
        await Search.RunForTagAsync(tag);
    }

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

        // Every write below happens AFTER an await, and a user can select again while one is in flight. Two
        // loads then race, and the one that started EARLIER can finish last and repaint the pane with the
        // previous subject — which is the same defect from the other end: the pane describing something other
        // than what is selected (ADR 0559). Found by #686's test, which saw a folder's title beside a
        // document's mask line.
        //
        // _selectedDocumentId was stamped above, so a stale load can see that it has been superseded and stop.
        bool Superseded() => _selectedDocumentId != document.Id;

        try
        {
            var mask = await _api.Documents.GetMaskAsync(document.Href("mask"));
            if (Superseded()) { return; }

            MaskLine = mask.Name is null ? "No mask" : $"Mask: {mask.Name}" + (mask.VersionNumber is { } v ? $" · version {v}" : "");

            var indexData = await _api.Documents.GetIndexDataAsync(document.Href("index-data"));
            if (Superseded()) { return; }

            foreach (var field in indexData)
            {
                IndexFields.Add(new IndexFieldViewModel { FieldName = field.FieldName, Values = string.Join(", ", field.Values) });
            }

            // A folder advertises no `versions`, and that absence is the ANSWER rather than a mistake — there
            // is nothing to preview and no current version to name. Asked with TryHref so a folder takes the
            // branch instead of throwing halfway through and leaving the pane half-loaded under a Status line.
            var versions = document.TryHref("versions");
            await LoadSystemFieldsAsync(document.DocumentSelfHref, versions, document.Name);
            if (Superseded()) { return; }

            if (versions is not null)
            {
                await LoadPreviewAsync(versions);
            }
            else
            {
                Preview.Reset(Strings.Get("NoPreview"));
            }

            if (document.TryHref("chat") is { } chat && !Superseded())
            {
                await LoadCommentsAsync(chat);
            }
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad2"), document.Name, e.Message);
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
    /// <summary>Grants this administrator full rights on one user's personal space (ADR 0672).</summary>
    public async Task TakeOverPersonalSpaceAsync(string spaceName, string takeOverHref)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Admin.TakeOverPersonalSpaceAsync(takeOverHref);
            Status = string.Format(Strings.Get("StTakenOver"), spaceName);
            await RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception)
        {
            Status = Strings.Get("StTakeOverFailed");
        }
    }

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

    // The duplicate-address-claim question (#703), provided by the view (the AnnotationDialog pattern): the
    // message names the mailbox already claiming the address; true = deliver to both, and the save retries
    // with the confirmation.
    public Func<string, Task<bool>>? ConfirmDuplicateClaimDialog { get; set; }

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

    [ObservableProperty] private string _tenantName = string.Empty;
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
    // The tenant default a NEW user's IMAP show-all preference seeds from (#793) — not a permission.
    [ObservableProperty] private bool _tenantImapShowAllDocumentsDefault;

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
    [ObservableProperty] private string _tenantStorageUsage = string.Empty;
    [ObservableProperty] private string _tenantStorageWarning = string.Empty;
    // Per-tenant bucket lifecycle: abort incomplete multipart uploads after N days (0 = off, ADR "Per-tenant
    // bucket policy knobs").
    [ObservableProperty] private int _tenantIncompleteUploadCleanupDays;
    // Audit webhook / SIEM streaming (ADR "Audit webhook streaming"). The secret is write-only; the box is left
    // blank on load and a non-empty value (re)sets it. TenantWebhookConfigured reports whether one is stored.
    [ObservableProperty] private string _tenantAuditWebhookUrl = string.Empty;
    [ObservableProperty] private string _tenantAuditWebhookSecret = string.Empty;
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
    private string _tenantWebhookHealth = string.Empty;
    public bool TenantWebhookHealthy { get; private set; }
    public bool TenantWebhookHealthVisible => !string.IsNullOrEmpty(TenantWebhookHealth);
    public Avalonia.Media.IBrush TenantWebhookHealthBrush => TenantWebhookHealthy ? HealthyBrush : FailingBrush;
    [ObservableProperty] private string _tenantOcrDisplay = string.Empty;
    [ObservableProperty] private string _tenantId = string.Empty;
    [ObservableProperty] private string _tenantStatus = string.Empty;
    [ObservableProperty] private string _tenantCreated = string.Empty;

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
            await (_ocrLanguages?.EnsureLoadedAsync() ?? Task.CompletedTask);
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
        TenantImapShowAllDocumentsDefault = s.ImapShowAllDocumentsDefault;
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
            TenantStorageWarning = string.Empty;
        }
        TenantIncompleteUploadCleanupDays = s.IncompleteUploadCleanupDays;
        TenantAuditWebhookUrl = s.AuditWebhookUrl ?? "";
        TenantAuditWebhookSecret = string.Empty;
        TenantWebhookConfigured = s.AuditWebhookConfigured;
        TenantWebhookHealthy = s.AuditWebhookConsecutiveFailures == 0;
        TenantWebhookHealth = DescribeWebhookHealth(s);
        _tenantStagedOcrCodes = s.DefaultOcrLanguages.Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
        TenantOcrDisplay = (_ocrLanguages?.Describe(_tenantStagedOcrCodes) ?? "");
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
        (_ocrLanguages?.Options ?? [], _tenantStagedOcrCodes);

    public void StageTenantOcrLanguages(IReadOnlyList<string> codes)
    {
        _tenantStagedOcrCodes = codes.ToList();
        TenantOcrDisplay = (_ocrLanguages?.Describe(_tenantStagedOcrCodes) ?? "");
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
    [ObservableProperty][NotifyPropertyChangedFor(nameof(UserInitials))] private string _userDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfilePhoto))]
    private Bitmap? _profilePhoto;

    public bool HasProfilePhoto => ProfilePhoto is not null;

    public string UserInitials => Initials(UserDisplayName);

    private Guid? _currentUserId;

    private static string Initials(string? name) => ContactInitials.From(name);

    private static Bitmap Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
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
    [ObservableProperty] private string _newTagName = string.Empty;
    [ObservableProperty] private string _newTagColor = string.Empty;

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
            NewTagName = string.Empty;
            NewTagColor = string.Empty;
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
    /// <param name="versionsHref">Null for a FOLDER, which advertises no versions (#686) — the system fields
    /// below the sensitivity/tags block are a current version's, so there are none to read.</param>
    private async Task LoadSystemFieldsAsync(string documentSelfHref, string? versionsHref, string name)
    {
        SysName = name;
        SysDocumentDate = null;
        SysCreated = string.Empty;
        SysCreatedBy = string.Empty;
        SysWorkflowStatus = null;
        WorkflowTransitions.Clear();
        SysFileExtension = string.Empty;
        SysHasTiff = false;
        SysOcrLanguages = string.Empty;
        SysCurrentVersion = string.Empty;
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
        catch (Exception) { _detailSensitivityName = string.Empty; _detailSensitivityColor = null; _detailSensitivityWatermark = false; DetailSensitivityId = null; _detailExternalLinksHref = null; CanShareDocument = false; }
        // Sensitivity watermark on the preview (ADR "Document watermarking") — when the label's watermark flag is set.
        Preview.WatermarkText = _detailSensitivityWatermark ? $"{_detailSensitivityName} · {UserDisplayName}" : "";
        // Whether the current user follows this document (ADR "Document subscriptions").
        try { DetailSubscribed = await _api.Reminders.GetSubscriptionAsync(DetailHref("subscription")); } catch (Exception) { DetailSubscribed = false; }

        // Free-form tags (ADR "Document tags").
        DetailTags.Clear();
        try { foreach (var t in await _api.Tags.GetTagsAsync(DetailHref("tags"))) DetailTags.Add(t); } catch (Exception) { /* leave empty */ }
        HasDetailTags = DetailTags.Count > 0;

        if (versionsHref is null)
        {
            return; // a folder: no versions rel at all, so nothing below this line applies
        }

        var fields = await _api.Documents.GetSystemFieldsAsync(versionsHref);
        if (fields is null)
        {
            return; // no confirmed version yet
        }

        _sysCurrentVersionId = fields.CurrentVersionId;
        _sysDocumentDateHref = fields.DocumentDateHref;
        SysCurrentVersion = fields.CurrentVersionNumber.ToString();
        SysCreated = fields.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        SysCreatedBy = fields.CreatedByName;
        SysWorkflowStatus = fields.WorkflowStatus;
        // The transitions the detail pane may offer (#691) — skipped for the states that offer nothing.
        await LoadWorkflowTransitionsAsync(fields.WorkflowStatus, versionsHref);
        SysFileExtension = fields.FileExtension;
        SysDocumentDate = DateTime.TryParse(fields.DocumentDate, out var d) ? d.Date : null;
        SysHasTiff = fields.HasTiffVersion;

        if (SysHasTiff)
        {
            await (_ocrLanguages?.EnsureLoadedAsync() ?? Task.CompletedTask);

            _sysOcrCodes = string.IsNullOrWhiteSpace(fields.OcrLanguages) ? [] : fields.OcrLanguages.Split('+', StringSplitOptions.RemoveEmptyEntries);
            _stagedOcrCodes = _sysOcrCodes;
            SysOcrLanguages = (_ocrLanguages?.Describe(_sysOcrCodes) ?? "");
        }
    }

    // Exposes the catalog + the currently staged ordered selection to the picker dialog (the view owns the
    // dialog). The picker stages into the pane; the pane's single Save persists it.
    public (IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> Catalog, IReadOnlyList<string> Selected) OcrLanguagePickerState() =>
        (_ocrLanguages?.Options ?? [], _stagedOcrCodes);

    // Stages the picker's ordered selection (no API call) — persisted by SaveDetail, discarded by cancel.
    public void StageOcrLanguages(IReadOnlyList<string> codes)
    {
        _stagedOcrCodes = codes;
        SysOcrLanguages = (_ocrLanguages?.Describe(codes) ?? "");
    }

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
            other.ReplyText = string.Empty;
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
            NewComment = string.Empty;
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
        DetailTitle = string.Empty;
        MaskLine = string.Empty;
        _detailSensitivityName = string.Empty;
        _detailSensitivityColor = null;
        _detailSensitivityWatermark = false;
        DetailSensitivityId = null;
        DetailSubscribed = false;
        DetailTags.Clear();
        HasDetailTags = false;
        IndexFields.Clear();
        Comments.Clear();
        Preview.WatermarkText = string.Empty;
        Preview.Reset("Select a document.");
        Preview.PreviewConverted = false;
        SysName = string.Empty;
        SysDocumentDate = null;
        SysCreated = string.Empty;
        SysCreatedBy = string.Empty;
        SysWorkflowStatus = null;
        WorkflowTransitions.Clear();
        SysFileExtension = string.Empty;
        SysHasTiff = false;
        SysOcrLanguages = string.Empty;
        SysCurrentVersion = string.Empty;
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

    // ---- Audit tab (ADR "Desktop audit viewer") — extracted to AuditTabViewModel (#517, tranche 1) --------

    // Gates the Audit TabItem's visibility (set from whoami on login); the tab's own state lives on Audit.
    [ObservableProperty] private bool _canViewAuditLog;

    public AuditTabViewModel Audit { get; }

}
