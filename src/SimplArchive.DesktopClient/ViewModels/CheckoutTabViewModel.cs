using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Check-out tab (ADR "Document check-out / check-in"; ADR 0513). Editing a checked-out document
// happens through the WebDAV mount, which writes the document's cloud stash — there is no local working copy any
// more (the pre-WebDAV local-folder model was retired, ADR 0513). So each row's "modified" is the SERVER's
// `IsModified` (SHA-256(stash) != version SHA), and Check-in is the stash-based server promotion; the desktop no
// longer downloads/uploads/hashes a local file. Edit opens the file via the WebDAV mount so saves flow to the stash.
public sealed partial class CheckoutTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;

    // Set by MainWindowViewModel to route messages to the shared bottom status bar.
    public Action<string>? StatusReporter { get; set; }

    // Invoked after a check-in / unlock / discard, so the Repositories list (lock glyphs) + the tab count refresh.
    public Func<Task>? OnChanged { get; set; }

    public void Setup(SimplArchiveApiClient api)
    {
        _api = api;
        Preview.Api = api;
        Preview.StatusReporter = m => Report(m);
    }

    // ---- Detail panes (ADR "The Check-out tab shows what you are about to check in") -------------------
    //
    // The tab used to be a bare table: to see what you had actually edited you left it, found the document in
    // Repositories, and looked at the ARCHIVED version — the one thing that is definitely not your edit. It now
    // has the Inbox tab's shape, a list beside index data over a preview, because they are the same kind of
    // place. The state lives here rather than on MainWindowViewModel, which is the largest entry on the
    // 1000-line debt list and where this would otherwise have added a dozen more properties.

    /// <summary>The working copy's preview — NOT the archived version (ADR 0543's `preview` rel on the row).</summary>
    public PreviewViewModel Preview { get; } = new();

    public ObservableCollection<IndexFieldViewModel> IndexFields { get; } = [];

    [ObservableProperty] private string _detailTitle = "";

    [ObservableProperty] private bool _maskCollapsed;

    [ObservableProperty] private bool _previewCollapsed;

    public string MaskCaret => MaskCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";

    public string PreviewCaret => PreviewCollapsed ? "mdi-chevron-down" : "mdi-chevron-up";

    [ObservableProperty] private GridLength _maskHeight = new(1.1, GridUnitType.Star);

    [ObservableProperty] private GridLength _previewHeight = new(1.6, GridUnitType.Star);

    partial void OnMaskCollapsedChanged(bool value) => OnPropertyChanged(nameof(MaskCaret));

    partial void OnPreviewCollapsedChanged(bool value) => OnPropertyChanged(nameof(PreviewCaret));

    [RelayCommand]
    private void ToggleMask() => MaskCollapsed = !MaskCollapsed;

    [RelayCommand]
    private void TogglePreview() => PreviewCollapsed = !PreviewCollapsed;

    /// <summary>The row whose working copy the detail panes describe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    [NotifyPropertyChangedFor(nameof(SelectedCanCheckIn))]
    [NotifyPropertyChangedFor(nameof(SelectedCanDiscard))]
    [NotifyPropertyChangedFor(nameof(SelectedCanUnlock))]
    [NotifyPropertyChangedFor(nameof(SelectedCanExtend))]
    private CheckoutRowViewModel? _selectedRow;

    // What the Check-out ribbon gates on (#521). The row's own Can* answer for the SELECTION, so a ribbon
    // button greys out for the same reasons its context-menu twin disappears — the two surfaces never disagree
    // about whether an action is possible, only about which item it means.
    //
    // With multi-select (#521's last piece) the gates split by what the verb MEANS across several documents.
    // Check in and discard are per-document verbs that compose — "check in the selection" is N check-ins with
    // one summary — so they gate on ANY selected row allowing them. Edit, compare, unlock and extend are
    // single-subject verbs (one working copy, one diff, one lease), so they additionally require the selection
    // to be exactly one: a button that would act on one of three highlighted rows is claiming a scope it does
    // not have, which is the ADR 0559 shape of lie.
    public bool HasSelectedRow => Selection.Count == 1;

    public bool SelectedCanCheckIn => Selection.Any(r => r.CanCheckIn);

    public bool SelectedCanDiscard => Selection.Any(r => r.CanDiscard);

    public bool SelectedCanUnlock => Selection.Count == 1 && SelectedRow?.CanUnlock == true;

    public bool SelectedCanExtend => Selection.Count == 1 && SelectedRow?.CanExtend == true;

    public bool SelectedIsSingleModified => Selection.Count == 1 && SelectedRow?.CanCheckIn == true;

    private IReadOnlyList<CheckoutRowViewModel> _selection = [];

    /// <summary>
    /// Every selected row, set by the view on SelectionChanged — SelectedRow alone cannot say how many. Falls
    /// back to the single SelectedRow so a selection made programmatically (tests, the headless screenshot
    /// renders) gates the ribbon the same way a clicked one does.
    /// </summary>
    public IReadOnlyList<CheckoutRowViewModel> Selection =>
        _selection.Count > 0 ? _selection : SelectedRow is { } one ? [one] : [];

    public void SetSelection(IReadOnlyList<CheckoutRowViewModel> rows)
    {
        _selection = rows;
        OnPropertyChanged(nameof(HasSelectedRow));
        OnPropertyChanged(nameof(SelectedCanCheckIn));
        OnPropertyChanged(nameof(SelectedCanDiscard));
        OnPropertyChanged(nameof(SelectedCanUnlock));
        OnPropertyChanged(nameof(SelectedCanExtend));
        OnPropertyChanged(nameof(SelectedIsSingleModified));
    }

    partial void OnSelectedRowChanged(CheckoutRowViewModel? value) => _ = LoadDetailAsync(value);

    private async Task LoadDetailAsync(CheckoutRowViewModel? row)
    {
        IndexFields.Clear();
        if (_api is null || row?.Item is not { } item)
        {
            DetailTitle = "";
            Preview.Reset(Strings.Get("SelectDocDetail"));
            return;
        }

        DetailTitle = row.DisplayName;
        Preview.Reset(Strings.Get("StLoading"));

        // Index data belongs to the DOCUMENT and is the same either side of an edit — a working copy carries no
        // metadata of its own. The preview is the half that must come from the working copy.
        try
        {
            foreach (var field in await _api.GetIndexDataAsync(row.Id))
            {
                IndexFields.Add(new IndexFieldViewModel
                {
                    FieldName = field.FieldName,
                    Values = string.Join(", ", field.Values),
                });
            }
        }
        catch (Exception)
        {
            // Index data is context, not the point of this pane; failing to read it must not cost the preview.
        }

        try
        {
            var preview = await _api.GetCheckoutPreviewAsync(item);
            if (preview is null)
            {
                // No working copy saved yet, or a format with no browser-viewable form. Both are ordinary.
                Preview.Reset(Strings.Get("NoPreview"));
                return;
            }

            await Preview.RenderAsync(preview);
        }
        catch (Exception)
        {
            Preview.Reset(Strings.Get("NoPreview"));
        }
    }

    public ObservableCollection<CheckoutRowViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public int Count => Items.Count;

    [ObservableProperty] private string _status = "";

    private void Report(string message)
    {
        Status = message;
        StatusReporter?.Invoke(message);
    }

    public async Task LoadAsync()
    {
        if (_api is null)
        {
            return;
        }

        Items.Clear();
        SelectedRow = null;
        try
        {
            foreach (var item in await _api.GetCheckoutsAsync())
            {
                Items.Add(new CheckoutRowViewModel
                {
                    Id = item.Id,
                    Name = item.Name,
                    Path = item.Path,
                    FileExtension = item.FileExtension,
                    IsModified = item.IsModified,
                    IsSigned = item.IsSigned,
                    ImplicitAgent = item.ImplicitAgent,
                    ExpiresAt = item.ExpiresAt,
                    StashDownloadUrl = item.StashDownloadUrl,
                    Item = item,
                });
            }

            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(Count));
            Status = Items.Count == 0 ? "No documents are checked out." : $"{Items.Count} document(s) checked out.";
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad"), e.Message);
        }
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    // Edit: open the checked-out file through the WebDAV mount (ADR 0513) so the native editor's saves flow straight
    // to the cloud stash — after which the next Refresh shows it as Modified and offers Check in. Best-effort: an
    // unconfigured WebDAV password or an unreachable mount is reported, never throws.
    [RelayCommand]
    private async Task Edit(CheckoutRowViewModel? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            var webdav = await _api.GetWebDavStatusAsync();
            if (!webdav.Enabled || string.IsNullOrWhiteSpace(webdav.Url))
            {
                Report(Strings.Get("CoEditNeedsWebDav"));
                return;
            }

            var fileName = MainWindowViewModel.WithExtension(row.Name, row.FileExtension);
            var result = await OsFileManager.OpenWebDavFileAsync(webdav.Url, $"Personal/Check-out/{fileName}");
            Report(result.Success
                ? string.Format(Strings.Get("CoEditing"), row.Name)
                : result.Error ?? $"Could not open '{row.Name}'.");
        }
        catch (Exception e)
        {
            Report($"Could not open '{row.Name}' for editing: {e.Message}");
        }
    }

    // Check in: the server promotes the cloud stash (the WebDAV-edited working copy) to a new confirmed version and
    // releases the lock (ADR 0513). Only offered when the row is Modified.
    [RelayCommand]
    private async Task CheckIn(CheckoutRowViewModel? row)
    {
        if (_api is null || row?.Item is not { } checkout)
        {
            return;
        }

        try
        {
            await _api.CheckInFromStashAsync(checkout);
            Report($"Checked in '{row.Name}'.");
            await ReloadAllAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not check in '{row.Name}': {e.Message}");
        }
    }

    // Extend: reset the auto-release idle timer (ADR "Self-service check-out extension") — keeps the lock, no
    // version, no stash change.
    [RelayCommand]
    private async Task Extend(CheckoutRowViewModel? row)
    {
        if (_api is null || row?.Item is not { } checkout)
        {
            return;
        }

        try
        {
            await _api.ExtendCheckoutAsync(checkout);
            Report($"Extended the check-out of '{row.Name}'.");
            await ReloadAllAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not extend '{row.Name}': {e.Message}");
        }
    }

    // Unlock: nothing to commit — release the lock (the server-side release also clears the stash).
    [RelayCommand]
    private Task Unlock(CheckoutRowViewModel? row) => ReleaseAsync(row, discard: false);

    // Discard: abandon the working copy — release the lock (which drops the stash) without a new version. The
    // confirmation dialog lives in the code-behind (data loss).
    public Task DiscardAsync(CheckoutRowViewModel row) => ReleaseAsync(row, discard: true);

    // ---- Bulk (#521's last piece): the ribbon's check-in and discard act on the whole selection. ----------
    //
    // No server bulk endpoint exists for check-out, so the client iterates and reports ONE summary in the
    // established bulk shape ("{ok} of {n}") rather than N status lines. A selected row that cannot take the
    // verb — an unmodified row under a bulk check-in — is skipped, not failed: the ribbon gate only promises
    // that SOME row can. And a failure does not stop the loop: the remaining documents are not hostages of the
    // first bad one, which is the partial-failure story the web's bulk-move path already tells. A single-row
    // selection routes through the single-row method so its wording and per-error reporting stay exactly what
    // they were.

    public async Task CheckInSelectionAsync(IReadOnlyList<CheckoutRowViewModel> rows)
    {
        if (rows.Count <= 1)
        {
            await CheckIn(rows.FirstOrDefault());
            return;
        }

        var eligible = rows.Where(r => r.CanCheckIn).ToList();
        var succeeded = 0;
        foreach (var row in eligible)
        {
            if (await TryCheckInAsync(row))
            {
                succeeded++;
            }
        }

        Report(string.Format(Strings.Get("CoBulkCheckedIn"), succeeded, eligible.Count));
        await ReloadAllAsync();
    }

    public async Task DiscardSelectionAsync(IReadOnlyList<CheckoutRowViewModel> rows)
    {
        if (rows.Count <= 1)
        {
            if (rows.FirstOrDefault() is { } single)
            {
                await DiscardAsync(single);
            }

            return;
        }

        var eligible = rows.Where(r => r.CanDiscard).ToList();
        var succeeded = 0;
        foreach (var row in eligible)
        {
            if (await TryDiscardAsync(row))
            {
                succeeded++;
            }
        }

        Report(string.Format(Strings.Get("CoBulkDiscarded"), succeeded, eligible.Count));
        await ReloadAllAsync();
    }

    private async Task<bool> TryCheckInAsync(CheckoutRowViewModel row)
    {
        if (_api is null || row.Item is not { } checkout)
        {
            return false;
        }

        try
        {
            await _api.CheckInFromStashAsync(checkout);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryDiscardAsync(CheckoutRowViewModel row)
    {
        if (_api is null)
        {
            return false;
        }

        try
        {
            await _api.CheckInAsync(row.Id); // DELETE the check-out — releases the lock + clears the stash
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ReleaseAsync(CheckoutRowViewModel? row, bool discard)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            await _api.CheckInAsync(row.Id); // DELETE the check-out — releases the lock + clears the stash server-side
            Report(discard ? $"Discarded the check-out of '{row.Name}'." : $"Released '{row.Name}'.");
            await ReloadAllAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not release '{row.Name}': {e.Message}");
        }
    }

    private async Task ReloadAllAsync()
    {
        await LoadAsync();
        if (OnChanged is not null)
        {
            await OnChanged();
        }
    }

    // Populates the Check-out tab for the headless screenshot (no network): one modified (Edit / Check in / Discard)
    // and one unchanged (Edit / Unlock).
    internal void PopulateDemoForScreenshot()
    {
        Items.Clear();
        Items.Add(new CheckoutRowViewModel { Id = Guid.NewGuid(), Name = "Contract draft", Path = "Repositories / Demo Repository / Contracts", FileExtension = ".docx", IsModified = true });
        // One row carries the "automatic" marker, so the screenshot path actually exercises it (ADR 0562) —
        // a marker no rendered surface ever shows is a marker nobody can check.
        Items.Add(new CheckoutRowViewModel { Id = Guid.NewGuid(), Name = "Price list", Path = "Repositories / Demo Repository / 2026", FileExtension = ".xlsx", IsModified = true, ImplicitAgent = "an office suite saving over WebDAV" });
        Items.Add(new CheckoutRowViewModel { Id = Guid.NewGuid(), Name = "Quarterly report", Path = "Repositories / Demo Repository / Finance", FileExtension = ".xlsx", IsModified = false });
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Count));
        Status = "2 document(s) checked out.";
    }
}

// One row in the Check-out tab: a checked-out document + its server-computed modification state (ADR 0513).
public sealed class CheckoutRowViewModel
{
    public required Guid Id { get; init; }

    // The row the server sent — check-in / extend / compare follow the addresses IT advertised (ADR 0543/0555).
    // Nullable because the designer-preview rows below are synthetic and reach no server; every real row has it.
    public SimplArchive.DesktopClient.Services.SimplArchiveApiClient.CheckoutItem? Item { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string FileExtension { get; init; }

    // The working copy in check-out (the cloud stash) differs from the current version — computed server-side.
    public required bool IsModified { get; init; }

    // Set when the lock was taken by a save-by-rename edit over the mount rather than by the user pressing
    // "check out" (ADR 0562) — null otherwise. Drives the row's "automatic" marker: a check-out nobody asked
    // for reads as a bug unless it explains itself.
    public string? ImplicitAgent { get; init; }

    public bool IsImplicit => !string.IsNullOrEmpty(ImplicitAgent);

    // The current version's content carries a digital signature (#491), examined at finalize. TRI-STATE: null
    // means the version was never examined — every version filed before this shipped — so the badge shows only
    // for a definite true, and an unexamined version says nothing rather than making a claim nobody checked.
    public bool? IsSigned { get; init; }

    public bool ShowSignedBadge => IsSigned == true;

    public string SignedTooltip => Strings.Get("SignedBadgeTip");

    public string ImplicitTooltip => string.Format(Strings.Get("CoAutoByTip"), ImplicitAgent);

    // Presigned GET for the working-copy stash (ADR 0517) — staged as the right-hand file for Beyond Compare.
    public string? StashDownloadUrl { get; init; }

    // Name shown in the list, WITH extension (ADR 0513): the archive stores a bare stem.
    public string DisplayName => Name + FileExtension;

    // When an idle check-out will be auto-released (ADR "Check-out expiry UX"); null when disabled.
    public DateTimeOffset? ExpiresAt { get; init; }
    public string ExpiresText => ExpiresAt is { } e
        ? e.LocalDateTime.ToString("g") + ((e - DateTimeOffset.UtcNow).TotalDays <= 1 ? " (soon)" : "")
        : "Never";

    public bool CanCheckIn => IsModified;
    public bool CanDiscard => IsModified;
    public bool CanUnlock => !IsModified; // unchanged — release without a new version
    public bool CanExtend => ExpiresAt is not null; // only meaningful when auto-release is enabled

    public string StatusText => IsModified ? Strings.Get("CoModified") : Strings.Get("CoUnchanged");
}
