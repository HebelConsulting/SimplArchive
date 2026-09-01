using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Intray tab: what has been staged for filing, and everything a user does to it before it lands.
/// </summary>
/// <remarks>
/// <para>
/// A tab view-model of its own, which #900 prepared by relocating this code into a partial so the boundary
/// could be READ before it was moved. What that made visible is how small the seam actually is: the tab wants
/// six things from the window around it, and they are the six on <see cref="IShellContext"/>.
/// </para>
/// <para>
/// Two of the four "shared helpers" #900 warned about turned out not to be shared at all —
/// <c>IsScannableExtension</c> is used only here (the shell had the declaration and no call), and
/// <c>LocalFolders</c> appeared solely inside that warning's own prose. Measuring beat remembering; the
/// remaining two, <c>DropFiling</c> and the OCR catalog, are genuinely shared and are reached through the
/// shell so there stays exactly one of each.
/// </para>
/// <para>
/// Members drop the <c>Intray</c> prefix the partial carried, because the type now says it: a binding reads
/// <c>Intray.Name</c>, not <c>Intray.IntrayName</c>. Done as its own change rather than folded into the
/// extraction — both are verified by a byte-identical render, and a render that changes tells you far more
/// when only one kind of thing moved.
/// </para>
/// <para>
/// <c>CanManageIntrays</c> keeps its name deliberately. It does not mean "this intray", it means the caller
/// may open OTHER users' intrays, and it is spelled the way the server's <c>whoami</c> flag is spelled.
/// <c>CanManage</c> would drop both the meaning and the correspondence.
/// </para>
/// </remarks>
public sealed partial class IntrayTabViewModel : ObservableObject
{
    private readonly IShellContext _shell;
    private SimplArchiveApiClient? _api;

    public IntrayTabViewModel(IShellContext shell)
    {
        _shell = shell;
        Preview = new PreviewViewModel(shell);
        Actions.Connect(() => _api, RefreshAsync, _shell.Report, () => _shell.CurrentUserId);
    }

    /// <summary>Hands the tab the session's API client. Called at login, which is later than construction.</summary>
    public void SetApi(SimplArchiveApiClient api)
    {
        _api = api;

        // The straightening toggle's state belongs to the USER, not the machine, so it is read from the server
        // once per session rather than restored from local settings (#491).
        Safe.Fire(async () => await Actions.LoadIngestPreferencesAsync());
    }

    // ---- the tab's four panes, persisted in the window's one layout file ------------------------------
    // The window still decides WHEN layout is reset/loaded/saved — it owns the file — but what those four rows
    // are is the tab's own business, so it answers rather than being reached into.

    internal void ResetLayout()
    {
        _serverSaved = new GridLength(DefaultServer, GridUnitType.Star);
        _localSaved = new GridLength(DefaultLocal, GridUnitType.Star);
        _maskSaved = new GridLength(DefaultMask, GridUnitType.Star);
        _previewSaved = new GridLength(DefaultPreview, GridUnitType.Star);
        ServerCollapsed = LocalCollapsed = MaskCollapsed = PreviewCollapsed = false;
        ServerHeight = _serverSaved;
        LocalHeight = _localSaved;
        MaskHeight = _maskSaved;
        PreviewHeight = _previewSaved;
    }

    internal void LoadLayout(LayoutSettings settings)
    {
        _serverSaved = GridLengths.ParseOrStar(settings.IntrayServerHeight, DefaultServer);
        _localSaved = GridLengths.ParseOrStar(settings.IntrayLocalHeight, DefaultLocal);
        _maskSaved = GridLengths.ParseOrStar(settings.IntrayMaskHeight, DefaultMask);
        _previewSaved = GridLengths.ParseOrStar(settings.IntrayPreviewHeight, DefaultPreview);

        ServerCollapsed = settings.IntrayServerCollapsed;
        LocalCollapsed = settings.IntrayLocalCollapsed;
        MaskCollapsed = settings.IntrayMaskCollapsed;
        PreviewCollapsed = settings.IntrayPreviewCollapsed;

        ServerHeight = ServerCollapsed ? new GridLength(0) : _serverSaved;
        LocalHeight = LocalCollapsed ? new GridLength(0) : _localSaved;
        MaskHeight = MaskCollapsed ? new GridLength(0) : _maskSaved;
        PreviewHeight = PreviewCollapsed ? new GridLength(0) : _previewSaved;
    }

    internal void WriteLayout(LayoutSettings settings)
    {
        settings.IntrayServerHeight = (ServerCollapsed ? _serverSaved : ServerHeight).ToString();
        settings.IntrayLocalHeight = (LocalCollapsed ? _localSaved : LocalHeight).ToString();
        settings.IntrayMaskHeight = (MaskCollapsed ? _maskSaved : MaskHeight).ToString();
        settings.IntrayPreviewHeight = (PreviewCollapsed ? _previewSaved : PreviewHeight).ToString();
        settings.IntrayServerCollapsed = ServerCollapsed;
        settings.IntrayLocalCollapsed = LocalCollapsed;
        settings.IntrayMaskCollapsed = MaskCollapsed;
        settings.IntrayPreviewCollapsed = PreviewCollapsed;
    }

    // Which files the scanning affordances apply to. Private: the shell declared this too, but never called it.
    private static bool IsScannableExtension(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".tif" or ".tiff" or ".pdf";

    // What a user can do TO a staged intray item — send it on, claim it, delete it, and take its pages apart or
    // together (#487). Extracted from this class rather than added to it: the actions are one cohesive thing,
    // and this file is over the 1000-line ceiling, so growing it further is not on the table (ADR 0575).
    public IntrayItemActionsViewModel Actions { get; } = new();

    [ObservableProperty] private GridLength _serverHeight;

    [ObservableProperty] private GridLength _localHeight;

    [ObservableProperty] private GridLength _maskHeight;

    [ObservableProperty] private GridLength _previewHeight;

    [ObservableProperty] private bool _serverCollapsed;

    [ObservableProperty] private bool _localCollapsed;

    [ObservableProperty] private bool _maskCollapsed;

    [ObservableProperty] private bool _previewCollapsed;

    private GridLength _serverSaved, _localSaved, _maskSaved, _previewSaved;

    private const double DefaultServer = 1, DefaultLocal = 1, DefaultMask = 1.1, DefaultPreview = 1.6;

    public string ServerCaret => ServerCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";

    public string LocalCaret => LocalCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";

    public string MaskCaret => MaskCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";

    public string PreviewCaret => PreviewCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";

    partial void OnServerCollapsedChanged(bool value) => OnPropertyChanged(nameof(ServerCaret));

    partial void OnLocalCollapsedChanged(bool value) => OnPropertyChanged(nameof(LocalCaret));

    partial void OnMaskCollapsedChanged(bool value) => OnPropertyChanged(nameof(MaskCaret));

    partial void OnPreviewCollapsedChanged(bool value) => OnPropertyChanged(nameof(PreviewCaret));

    [RelayCommand]
    private void ToggleServer()
    {
        if (ServerCollapsed) { ServerHeight = _serverSaved; ServerCollapsed = false; }
        else { _serverSaved = ServerHeight; ServerHeight = new GridLength(0); ServerCollapsed = true; }
        _shell.SaveLayout();
    }

    [RelayCommand]
    private void ToggleLocal()
    {
        if (LocalCollapsed) { LocalHeight = _localSaved; LocalCollapsed = false; }
        else { _localSaved = LocalHeight; LocalHeight = new GridLength(0); LocalCollapsed = true; }
        _shell.SaveLayout();
    }

    [RelayCommand]
    private void ToggleMask()
    {
        if (MaskCollapsed) { MaskHeight = _maskSaved; MaskCollapsed = false; }
        else { _maskSaved = MaskHeight; MaskHeight = new GridLength(0); MaskCollapsed = true; }
        _shell.SaveLayout();
    }

    [RelayCommand]
    private void TogglePreview()
    {
        if (PreviewCollapsed) { PreviewHeight = _previewSaved; PreviewCollapsed = false; }
        else { _previewSaved = PreviewHeight; PreviewHeight = new GridLength(0); PreviewCollapsed = true; }
        _shell.SaveLayout();
    }

    // The Intray tab owns a SEPARATE preview instance so its preview never entangles the Repositories one — a
    // preview shown on one tab must not leak onto the other (mirrors RecycleBin.Preview). Bound by the Intray
    // PreviewPane.
    public PreviewViewModel Preview { get; }

    public ObservableCollection<IntrayItemViewModel> ServerItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanSendSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanClaimSelectedItem))]
    private IntrayItemViewModel? _selectedServerItem;

    // What the Intray ribbon's selected-item group is gated on (#521). Greyed rather than hidden when there is
    // no selection: the button is available to this user, it simply has nothing to act on yet, and hiding it
    // would make the ribbon reflow under the cursor as the selection changes (the Repositories tab's rule).
    public bool HasSelectedItem => SelectedServerItem is not null;

    /// <summary>"Send to…" is for your OWN item; somebody else's is claimed instead (ADR 0532).</summary>
    public bool CanSendSelectedItem => SelectedServerItem?.IsOwn == true;

    /// <summary>And the mirror: a group or other-user item is the one you can move to your own intray.</summary>
    public bool CanClaimSelectedItem => SelectedServerItem is { IsOwn: false };

    [ObservableProperty] private string _status = string.Empty;

    // Whether the caller holds CanManageIntrays (own or via a group) — gates the intray user-picker that opens
    // another user's intray for triage (ADR 0532); set from whoami on login.
    [ObservableProperty] private bool _canManageIntrays;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_api is null)
        {
            return;
        }

        ServerItems.Clear();
        try
        {
            // The admin user-picker's choices (CanManageIntrays only) — loaded once, "My intray" first (null id).
            if (CanManageIntrays && Users.Count == 0)
            {
                Users.Add(new IntrayUserPickerItem(null, Strings.Get("IntrayMine")));
                foreach (var u in await _api.Intray.GetIntrayUsersAsync())
                {
                    Users.Add(new IntrayUserPickerItem(u.Id, u.Name));
                }
            }

            // One read, many follows (ADR 0557): the listing carries the rows AND the collection's own `join`
            // address, so the Join action costs no extra request when a selection later enables it.
            var listing = await _api.Intray.ListAsync(IncludeGroups, ViewUserId);
            Actions.JoinHref = listing.Href("join");
            Actions.PatchCodeSheetHref = listing.Href("patchCodeSheet");
            foreach (var item in listing.Items)
            {
                ServerItems.Add(new IntrayItemViewModel
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
            _shell.Report(string.Format(Strings.Get("StErrLoadIntray"), ex.Message));
        }

        Status = string.Format(Strings.Get("StItems"), ServerItems.Count);

        // A refresh rebuilds the list, so nothing is focused — clear the right panes.
        SelectedServerItem = null;
    }

    [ObservableProperty] private bool _includeGroups;

    [ObservableProperty] private Guid? _viewUserId;

    // The user-picker choices (only populated for a CanManageIntrays holder); the first is "My intray" (null id).
    public ObservableCollection<IntrayUserPickerItem> Users { get; } = [];

    [ObservableProperty] private IntrayUserPickerItem? _selectedUser;

    // Suppresses the reentrant refresh when one filter handler adjusts the other (the two are mutually exclusive).
    private bool _adjustingFilters;

    // The "Show group intrays" checkbox — reveals my group intrays; clears any admin user-view (they're exclusive).
    async partial void OnIncludeGroupsChanged(bool value)
    {
        if (_adjustingFilters)
        {
            return;
        }

        _adjustingFilters = true;
        if (value)
        {
            ViewUserId = null;
            SelectedUser = Users.Count > 0 ? Users[0] : null; // back to "My intray"
        }

        _adjustingFilters = false;
        await RefreshAsync();
    }

    // The admin user-picker — open a chosen user's intray, or (null id) back to my own.
    async partial void OnSelectedUserChanged(IntrayUserPickerItem? value)
    {
        if (_adjustingFilters)
        {
            return;
        }

        _adjustingFilters = true;
        ViewUserId = value?.UserId;
        if (value is { UserId: not null })
        {
            IncludeGroups = false;
        }

        _adjustingFilters = false;
        await RefreshAsync();
    }

    public async Task CopyDocumentsAsync(IReadOnlyList<Guid> documentIds)
    {
        if (_shell.DropFiling is not { } dropFiling)
        {
            return;
        }

        if (await dropFiling.CopyToIntrayAsync(documentIds, _shell.Report) > 0)
        {
            await RefreshAsync();
            _shell.ActivateIntray();   // the tree lists FOLDERS and can never show what just landed
        }
    }

    public async Task UploadFilesAsync(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        if (_api is null || files.Count == 0)
        {
            return;
        }

        Status = string.Format(Strings.Get("StUploadingN"), files.Count);
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
                _shell.Report(string.Format(Strings.Get("StErrUpload2b"), name, ex.Message));
            }
        }

        await RefreshAsync();
        if (uploaded > 0)
        {
            Status = string.Format(Strings.Get("StUploadedAndItems"), uploaded, ServerItems.Count);
        }
    }

    [ObservableProperty] private bool _itemFocused;

    [ObservableProperty] private bool _isEmail; // .eml/.msg → classified by the system, no mask offered
    [ObservableProperty] private string _detailTitle = string.Empty;

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private DateTime? _documentDate;

    [ObservableProperty] private MaskChoiceViewModel? _selectedMaskChoice;

    public ObservableCollection<MaskChoiceViewModel> AvailableMasks { get; } = [];

    public ObservableCollection<MaskFieldEditViewModel> MaskEditFields { get; } = [];

    private Dictionary<Guid, IReadOnlyList<string>> _draftValues = [];

    private bool _loadingMask;

    // Loads the right panes when a server intray item gains focus (or clears them when focus is lost).
    async partial void OnSelectedServerItemChanged(IntrayItemViewModel? value)
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

        DetailTitle = value.Name;
        Preview.Reset("Loading…");
        Preview.FindQuery = string.Empty;

        // An email (.eml/.msg) is classified automatically by the system when filed — the mask isn't offered
        // in the intray for it (ADR "Consume the staged mask sidecar at filing").
        var extension = Path.GetExtension(value.Name);
        IsEmail = extension.Equals(".eml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msg", StringComparison.OrdinalIgnoreCase);

        try
        {
            await Preview.RenderAsync(await _api.Intray.GetIntrayPreviewAsync(value.Item!));
            if (!IsEmail)
            {
                await LoadIntrayMaskAsync(value.Item!);
            }

            ItemFocused = true;
        }
        catch (Exception e)
        {
            _shell.Report(string.Format(Strings.Get("StErrLoad2"), value.Name, e.Message));
        }
    }

    private async Task LoadIntrayMaskAsync(IntrayApi.IntrayItemInfo item)
    {
        _loadingMask = true;
        try
        {
            AvailableMasks.Clear();
            AvailableMasks.Add(new MaskChoiceViewModel(null, "(No mask)"));
            foreach (var mask in await _api!.Masks.GetMasksAsync())
            {
                AvailableMasks.Add(new MaskChoiceViewModel(mask.Id, mask.Name, mask));
            }

            var name = item.Name;
            var draft = await _api.Intray.GetIntrayMaskAsync(item);
            _draftValues = draft.Fields.ToDictionary(f => f.FieldDefinitionId, f => f.Values);
            Name = string.IsNullOrEmpty(draft.Name) ? Path.GetFileNameWithoutExtension(name) : draft.Name;
            DocumentDate = DateTime.TryParse(draft.DocumentDate, out var d) ? d.Date : null;

            // OCR languages: only for a scannable item, staged + applied at filing (ADR "Inbox OCR-language
            // staging"). Load the catalog on demand so DescribeOcrLanguages can map codes → names.
            StgScannable = IsScannableExtension(name);
            _stgOcrCodes = draft.OcrLanguages.ToList();
            if (StgScannable)
            {
                await (_shell.OcrLanguages?.EnsureLoadedAsync() ?? Task.CompletedTask);
            }
            OcrDisplay = (_shell.OcrLanguages?.Describe(_stgOcrCodes) ?? "");

            // Preselect the staged mask, or default to "Basic Entry" for an un-classified item (the same
            // default auto-classification applies at filing).
            SelectedMaskChoice = draft.MaskId is { } staged
                ? AvailableMasks.FirstOrDefault(m => m.MaskId == staged) ?? AvailableMasks[0]
                : AvailableMasks.FirstOrDefault(m => m.Name == "Basic Entry") ?? AvailableMasks[0];
            await LoadIntrayMaskFieldsAsync(SelectedMaskChoice?.Mask, useDraftValues: true);
        }
        finally
        {
            _loadingMask = false;
        }
    }

    // Reloads the field editors when a different mask is picked (empty values); suppressed on the initial load,
    // which fills the staged draft values instead.
    async partial void OnSelectedMaskChoiceChanged(MaskChoiceViewModel? value)
    {
        if (_loadingMask)
        {
            return;
        }

        await LoadIntrayMaskFieldsAsync(value?.Mask, useDraftValues: false);
    }

    private async Task LoadIntrayMaskFieldsAsync(MasksClient.MaskOptionInfo? mask, bool useDraftValues)
    {
        MaskEditFields.Clear();
        if (_api is null || mask is not { } chosen)
        {
            return;
        }

        foreach (var field in await _api.Masks.GetMaskFieldsAsync(chosen))
        {
            var values = useDraftValues && _draftValues.TryGetValue(field.Id, out var v) ? v : [];
            MaskEditFields.Add(MaskFieldEditViewModel.Create(field, values));
        }
    }

    // Saves the staged mask/index-data to the focused item's `{name}.mask.json` sidecar (no filed Document yet,
    // so no required-field validation runs here). Updates the item's square-bracket indicator in place.
    [RelayCommand]
    private async Task SaveMaskAsync()
    {
        if (_api is null || SelectedServerItem is not { } item)
        {
            return;
        }

        try
        {
            var maskId = SelectedMaskChoice?.MaskId;
            var fields = MaskEditFields.Select(f => (f.FieldDefinitionId, f.ToValues())).ToList();
            var stagedName = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
            var docDate = DocumentDate?.ToString("yyyy-MM-dd");
            var ocr = StgScannable && _stgOcrCodes.Count > 0 ? _stgOcrCodes : null;
            await _api.Intray.SetIntrayMaskAsync(item.Item!, stagedName, docDate, maskId, fields, ocr);
            item.HasMask = maskId is not null || fields.Any(f => f.Item2.Count > 0) || stagedName is not null || docDate is not null || ocr is not null;
            _shell.Report(Strings.Get("StMaskSaved"));
        }
        catch (Exception e)
        {
            _shell.Report(string.Format(Strings.Get("StErrSaveMask"), e.Message));
        }
    }

    // The intray mask pane's OCR-language picker (ADR "Inbox OCR-language staging") — shown only for a scannable
    // item (.tif/.tiff/.pdf); edited via the view's OnEditIntrayOcrLanguages (the shared OcrLanguagePickerDialog),
    // staged into the pane, and consumed at filing to OCR the searchable-PDF successor in the chosen languages.
    [ObservableProperty] private bool _stgScannable;

    [ObservableProperty] private string _ocrDisplay = string.Empty;

    private List<string> _stgOcrCodes = [];

    public (IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> Catalog, IReadOnlyList<string> Selected) OcrPickerState() =>
        (_shell.OcrLanguages?.Options ?? [], _stgOcrCodes);

    public void StageOcrLanguages(IReadOnlyList<string> codes)
    {
        _stgOcrCodes = codes.ToList();
        OcrDisplay = (_shell.OcrLanguages?.Describe(_stgOcrCodes) ?? "");
    }

    private void ClearIntrayDetail()
    {
        ItemFocused = false;
        IsEmail = false;
        DetailTitle = string.Empty;
        Name = string.Empty;
        DocumentDate = null;
        AvailableMasks.Clear();
        MaskEditFields.Clear();
        _draftValues = [];
        Preview.Reset("Select a server intray item.");
        Preview.PreviewConverted = false;
        Preview.CanFindInDocument = false;
        Preview.FindQuery = string.Empty;
    }

    // Opens a server intray item natively: download it to the temp folder, then hand it to its OS app.
    [RelayCommand]
    private async Task OpenServerItemAsync()
    {
        if (SelectedServerItem is not { } item)
        {
            return;
        }

        try
        {
            await NativeFileOpener.OpenAsync(item.DownloadUrl, item.Name);
            _shell.Report(string.Format(Strings.Get("StOpened"), item.Name));
        }
        catch (Exception ex)
        {
            _shell.Report(string.Format(Strings.Get("StErrOpen2"), item.Name, ex.Message));
        }
    }

    // Files a server intray item into a chosen folder (the view picks it), then refreshes.
    public async Task FileServerItemAsync(IntrayItemViewModel item, Guid folderId, string? comment)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Intray.FileIntrayItemAsync(item.Item!, folderId, comment);
            _shell.Report(string.Format(Strings.Get("StFiled"), item.Name));
            await RefreshAsync();
        }
        catch (ApiActionException e)
        {
            _shell.Report(e.Message);
        }
    }

    // Files a server intray item as a new version of an existing document (ADR "Context-aware inbox filing dialog").
    public async Task FileServerItemAsVersionAsync(IntrayItemViewModel item, Guid documentId, string? comment)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Intray.FileIntrayItemAsVersionAsync(item.Item!, documentId, comment);
            _shell.Report(string.Format(Strings.Get("StFiledVersion"), item.Name));
            await RefreshAsync();

            // The server posts a feed comment on the filed document and adds a new version (ADR "Filing posts a
            // feed comment"). If that document is the one currently open on the Repositories tab, refresh its
            // detail so the comment + the new version's preview show without a manual reselect.
            await _shell.DocumentChangedOnServerAsync(documentId);
        }
        catch (ApiActionException e)
        {
            _shell.Report(e.Message);
        }
    }

    // The Intray half of the screenshot demo. The shell keeps the half that is its own state — signed-in,
    // which tab is in front — so neither side needs a hook into the other just to pose for a picture.
    internal void PopulateDemo()
    {
        ServerItems.Add(new IntrayItemViewModel { Name = "invoice-2026-03.pdf", Size = 132_004, DownloadUrl = string.Empty, HasMask = true });
        ServerItems.Add(new IntrayItemViewModel { Name = "meeting-notes.eml", Size = 8_942, DownloadUrl = string.Empty, HasMask = false });
        Status = "2 item(s).";

        // Focus the first server item so the right panes (mask + preview) show in the screenshot.
        DetailTitle = "invoice-2026-03.pdf";
        ItemFocused = true;
        Name = "invoice-2026-03";
        DocumentDate = new DateTime(2026, 3, 31);
        AvailableMasks.Add(new MaskChoiceViewModel(null, "(No mask)"));
        AvailableMasks.Add(new MaskChoiceViewModel(Guid.NewGuid(), "Basic Entry"));
        SelectedMaskChoice = AvailableMasks[1];
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), "Keywords", "MultiSelect", false), ["invoice", "march"]));
    }

    // Headless exercise of the intray drop-zone upload (ADR "Inbox file-list drop-zone", see DesktopIntrayDropTests):
    // uploading dropped bytes puts a new item in the server intray. Cleans up so the shared demo intray stays tidy.
    internal async Task<bool> DropSelfTestAsync()
    {
        var name = "intraydrop-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        await UploadFilesAsync(new[] { (name, System.Text.Encoding.UTF8.GetBytes("dropped into the intray")) });
        var present = ServerItems.Any(i => i.Name == name);
        if (ServerItems.FirstOrDefault(i => i.Name == name) is { } uploaded)
        {
            await _api!.Intray.DeleteIntrayItemAsync(uploaded.Item!);
        }

        return present;
    }

    // Headless exercise of intray send + admin triage (ADR 0532, see DesktopIntraySendTests): the admin uploads an
    // own item, hands it to a freshly-created user via the send-target list, and — as a CanManageIntrays holder —
    // sees it in that user's intray via ?user=. Cleans up the item + the user so the shared demo stays tidy.
    internal async Task<bool> SendSelfTestAsync()
    {
        CanManageIntrays = (await _api!.GetWhoAmIAsync()).CanManageIntrays;

        var recipient = await _api.Admin.CreateUserAsync($"send-{Guid.NewGuid():N}@e2e.local", "Send Recipient");

        var name = "send-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        await UploadFilesAsync(new[] { (name, System.Text.Encoding.UTF8.GetBytes("hand-off")) });
        if (ServerItems.FirstOrDefault(i => i.Name == name) is not { } item)
        {
            return false;
        }

        var target = (await Actions.GetSendTargetsAsync()).FirstOrDefault(t => !t.IsGroup && t.Id == recipient.Id);
        if (target is null)
        {
            return false;
        }

        await Actions.SendAsync(item, target);
        var leftOwnIntray = ServerItems.All(i => i.Name != name);                                  // gone from mine
        var inRecipientIntray = (await _api.Intray.ListAsync(user: recipient.Id)).Items.Any(i => i.Name == name); // now theirs

        if ((await _api.Intray.ListAsync(user: recipient.Id)).Items.FirstOrDefault(i => i.Name == name) is { } handedOver)
        {
            await _api.Intray.DeleteIntrayItemAsync(handedOver);
        }

        await _api.Admin.DeleteUserAsync(recipient);
        return leftOwnIntray && inRecipientIntray;
    }
}
