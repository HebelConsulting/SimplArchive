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
/// The Intray tab's state and behaviour — a partial of <see cref="MainWindowViewModel"/>, not yet a class of
/// its own (#517).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a partial rather than the IntrayTabViewModel the issue asks for.</b> Two attempts at the real
/// extraction were reset. The Intray code is interleaved with shell code at METHOD granularity, and every
/// boundary drawn by moving things exposed another crossing: the section banners overstate the tab by ~430
/// lines (session rights, check-out, reconnect and the TIFF backfill are filed under Intray headings), a
/// name-based selection misses <c>StashDroppedFilesAsync</c> and every <c>FileServerIntrayItem*</c>, and
/// compiler-closure kept dragging in shared helpers — <c>UseApi</c>, <c>_dropFiling</c>,
/// <c>IsScannableExtension</c>, <c>LocalFolders</c>.
/// </para>
/// <para>
/// So this step makes the boundary VISIBLE before moving it. Same class, so nothing here needed a decision
/// about what is shared: a helper serving both the Intray and the shell simply stayed where it was, and no
/// binding, provider or view had to change. What the tab reaches for is now a diff rather than an
/// archaeology exercise — which is what the real extraction needs in order to be reviewable.
/// </para>
/// <para>
/// The four Intray PANE heights are here too, though the window's Save/Load/Reset still drive them: they are
/// Intray's panes, and being in the same class means that costs nothing to say.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    // What a user can do TO a staged intray item — send it on, claim it, delete it, and take its pages apart or
    // together (#487). Extracted from this class rather than added to it: the actions are one cohesive thing,
    // and this file is over the 1000-line ceiling, so growing it further is not on the table (ADR 0575).
    public IntrayItemActionsViewModel IntrayActions { get; } = new();

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

    // The Intray tab owns a SEPARATE preview instance so its preview never entangles the Repositories one — a
    // preview shown on one tab must not leak onto the other (mirrors RecycleBin.Preview). Bound by the Intray
    // PreviewPane.
    public PreviewViewModel IntrayPreview { get; } = new();

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

    // Whether the caller holds CanManageIntrays (own or via a group) — gates the intray user-picker that opens
    // another user's intray for triage (ADR 0532); set from whoami on login.
    [ObservableProperty] private bool _canManageIntrays;

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
            // The admin user-picker's choices (CanManageIntrays only) — loaded once, "My intray" first (null id).
            if (CanManageIntrays && IntrayUsers.Count == 0)
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

    [ObservableProperty] private bool _intrayIncludeGroups;

    [ObservableProperty] private Guid? _intrayViewUserId;

    // The user-picker choices (only populated for a CanManageIntrays holder); the first is "My intray" (null id).
    public ObservableCollection<IntrayUserPickerItem> IntrayUsers { get; } = [];

    [ObservableProperty] private IntrayUserPickerItem? _selectedIntrayUser;

    // Suppresses the reentrant refresh when one filter handler adjusts the other (the two are mutually exclusive).
    private bool _adjustingIntrayFilters;

    // The "Show group intrays" checkbox — reveals my group intrays; clears any admin user-view (they're exclusive).
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
            foreach (var mask in await _api!.Masks.GetMasksAsync())
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
            if (IntrayStgScannable)
            {
                await (_ocrLanguages?.EnsureLoadedAsync() ?? Task.CompletedTask);
            }
            IntrayOcrDisplay = (_ocrLanguages?.Describe(_intrayStgOcrCodes) ?? "");

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

    private async Task LoadIntrayMaskFieldsAsync(MasksClient.MaskOptionInfo? mask, bool useDraftValues)
    {
        IntrayMaskEditFields.Clear();
        if (_api is null || mask is not { } chosen)
        {
            return;
        }

        foreach (var field in await _api.Masks.GetMaskFieldsAsync(chosen))
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
        (_ocrLanguages?.Options ?? [], _intrayStgOcrCodes);

    public void StageIntrayOcrLanguages(IReadOnlyList<string> codes)
    {
        _intrayStgOcrCodes = codes.ToList();
        IntrayOcrDisplay = (_ocrLanguages?.Describe(_intrayStgOcrCodes) ?? "");
    }

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
        IntrayMaskEditFields.Add(MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), "Keywords", "MultiSelect", false), ["invoice", "march"]));
        Preview.Reset("Preview renders here (PDF/image/text).");
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
    // own item, hands it to a freshly-created user via the send-target list, and — as a CanManageIntrays holder —
    // sees it in that user's intray via ?user=. Cleans up the item + the user so the shared demo stays tidy.
    internal async Task<bool> IntraySendSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        CanManageIntrays = (await _api!.GetWhoAmIAsync()).CanManageIntrays;

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
}
