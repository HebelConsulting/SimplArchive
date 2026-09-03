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
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanEditOcr))] private bool _sysOcrCandidate;
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
    public async Task InitializeSessionAsync(SimplArchiveApiClient api, string email)
    {
        UseApi(api);
        UserEmail = email;
        IsLoggedIn = true;
        await SetupUserContextAsync();
        await LoadRootAsync();
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
    // ---- Workflow + tasks (ADR "Workflow / document state model", 0009) -------------------------------
    // ---- In-app notifications bell (ADR "Notification viewer + click-through") -----------------------

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
        SysOcrCandidate = false;
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
