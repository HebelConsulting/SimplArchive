using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Recycle bin tab (ADR "Desktop recycle bin parity", mirroring the web ADR 0329): a tenant-
// wide master-detail — the list of soft-deleted documents (Name / Path / DeletedAt / DeletedBy + per-row
// Restore + tenant-admin Hard-delete) on the left, and the same read-only detail + preview + chat as the
// Repositories tab on the right. Its own PreviewViewModel keeps the recycle-bin preview from ever entangling
// the Repositories/Inbox one (the explicit isolation requirement).
public sealed partial class RecycleBinTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;

    // What the BIN itself offers — restore-selected / purge-selected / purge-all — captured where the
    // collection is read, so a tab full of buttons costs no extra request (ADR 0557).
    private IReadOnlyDictionary<string, string> _binLinks = new Dictionary<string, string>();

    private string BinRel(string rel) => _binLinks.TryGetValue(rel, out var href)
        ? href
        : throw new InvalidOperationException($"The recycle bin advertised no '{rel}' rel (ADR 0543).");

    // Set by MainWindowViewModel to route messages to the shared bottom status bar.
    public Action<string>? StatusReporter { get; set; }

    public RecycleBinTabViewModel()
    {
        // Route preview hit-word-copy confirmations through this tab's status too.
        Preview.StatusReporter = Report;
    }

    // This tab's INDEPENDENT preview surface (never shared with Repositories/Inbox — ADR "Desktop recycle bin
    // parity"). Read-only: a deleted item is inspected, not edited.
    public PreviewViewModel Preview { get; } = new();

    public void SetApi(SimplArchiveApiClient api)
    {
        _api = api;
        Preview.Api = api;
    }

    // Tenant-admin gates the per-row Hard-delete + Hard-delete all (permanent purge, ADR 0328/0329). Restore is
    // available to anyone who can see the recycled items.
    [ObservableProperty] private bool _isTenantAdmin;

    [ObservableProperty] private string _status = "";

    public ObservableCollection<RecycleBinRowViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RecycleBinRowViewModel? _selectedItem;

    public bool HasSelection => SelectedItem is not null;

    // ---- Read-only detail (mirrors the Repositories detail pane, no edit) -----------------------------

    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _maskLine = "";
    [ObservableProperty] private string _sysName = "";
    [ObservableProperty] private string _sysDocumentDate = "";
    [ObservableProperty] private string _sysCreated = "";
    [ObservableProperty] private string _sysCreatedBy = "";
    [ObservableProperty] private string _sysFileExtension = "";
    [ObservableProperty] private string _sysOcrLanguages = "";

    public ObservableCollection<IndexFieldViewModel> IndexFields { get; } = [];
    public ObservableCollection<ChatMessageViewModel> Comments { get; } = [];

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
        ClearDetail();
        try
        {
            var bin = await _api.GetRecycleBinItemsAsync();
            _binLinks = bin.Links;
            foreach (var item in bin.Items)
            {
                Items.Add(new RecycleBinRowViewModel
                {
                    Entry = item,
                    Id = item.Id,
                    Name = item.Name,
                    Path = item.Path,
                    DeletedAt = item.DeletedAt,
                    DeletedBy = item.DeletedBy,
                    IsTenantAdmin = IsTenantAdmin, // gates the row menu's hard-delete (#530)
                });
            }

            SetSelection([]); // a reload invalidates whatever was highlighted
            OnPropertyChanged(nameof(HasItems));
            Status = Items.Count == 0 ? "The recycle bin is empty." : string.Format(Strings.Get("StDeletedItems"), Items.Count);
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad"), e.Message);
        }
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    async partial void OnSelectedItemChanged(RecycleBinRowViewModel? value)
    {
        if (value is null || _api is null)
        {
            ClearDetail();
            return;
        }

        DetailTitle = value.Name;
        IndexFields.Clear();
        Comments.Clear();
        Preview.Reset("Loading…");
        Preview.FindQuery = "";

        try
        {
            // These read endpoints serve soft-deleted documents (ADR "Recycle bin tab") — read-only inspection.
            var mask = await _api.Documents.GetMaskAsync(value.Id);
            MaskLine = mask.Name is null ? "No mask" : $"Mask: {mask.Name}" + (mask.VersionNumber is { } v ? $" · version {v}" : "");

            foreach (var field in await _api.Documents.GetIndexDataAsync(value.Id))
            {
                IndexFields.Add(new IndexFieldViewModel { FieldName = field.FieldName, Values = string.Join(", ", field.Values) });
            }

            var fields = await _api.Documents.GetSystemFieldsAsync(value.Id);
            SysName = value.Name;
            SysCreated = fields is null ? "" : fields.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            SysCreatedBy = fields?.CreatedByName ?? "";
            SysFileExtension = fields?.FileExtension ?? "";
            SysDocumentDate = fields?.DocumentDate ?? "";
            SysOcrLanguages = fields?.OcrLanguages ?? "";

            await Preview.RenderAsync(await _api.Documents.GetPreviewAsync(value.Id));
            await LoadCommentsAsync(value.Id);
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad2"), value.Name, e.Message);
        }
    }

    private async Task LoadCommentsAsync(Guid documentId)
    {
        var comments = await _api!.Documents.GetCommentsAsync(documentId);
        var byId = comments.ToDictionary(
            c => c.Id,
            c => new ChatMessageViewModel { Id = c.Id, AuthorName = c.AuthorName, Body = c.Body, CreatedAt = c.CreatedAt, AuthorCardHref = c.AuthorCardHref, Kind = c.Kind, VersionNumber = c.VersionNumber, VersionComment = c.VersionComment, VersionCommentKind = c.VersionCommentKind });

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

    private void ClearDetail()
    {
        DetailTitle = "";
        MaskLine = "";
        SysName = "";
        SysDocumentDate = "";
        SysCreated = "";
        SysCreatedBy = "";
        SysFileExtension = "";
        SysOcrLanguages = "";
        IndexFields.Clear();
        Comments.Clear();
        Preview.Reset("Select a deleted item.");
        Preview.PreviewConverted = false;
    }

    [RelayCommand]
    private async Task Restore(RecycleBinRowViewModel? item)
    {
        if (_api is null || item is null)
        {
            return;
        }

        try
        {
            await _api.Documents.RestoreAsync(item.Entry!);
            Report($"Restored '{item.Name}'.");
            await LoadAsync();
        }
        catch (Exception e)
        {
            Report($"Could not restore '{item.Name}': {e.Message}");
        }
    }

    // ---- Bulk restore / bulk purge, over the native multi-SELECTION (#530 tranche 1). --------------------
    //
    // The checked-set column and its select-all reconcile machinery are gone: the list multi-selects the way
    // Repositories, Inbox and Check-out already do, the view pushes the selection here, and the ribbon's bulk
    // buttons act on it. Falls back to the single SelectedItem so a programmatic selection (tests, the
    // headless screenshot) gates identically — the CheckoutTabViewModel recipe.
    private IReadOnlyList<RecycleBinRowViewModel> _selection = [];

    public IReadOnlyList<RecycleBinRowViewModel> Selection =>
        _selection.Count > 0 ? _selection : SelectedItem is { } one ? [one] : [];

    public void SetSelection(IReadOnlyList<RecycleBinRowViewModel> rows)
    {
        _selection = rows;
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasCheckedItems));
        OnPropertyChanged(nameof(RestoreSelectedLabel));
        OnPropertyChanged(nameof(CanPurgeSelected));
        OnPropertyChanged(nameof(PurgeSelectedLabel));
    }

    public int CheckedCount => Selection.Count;
    public bool HasCheckedItems => Selection.Count > 0;
    public string RestoreSelectedLabel => $"Restore selected ({CheckedCount})";
    // Bulk purge is tenant-admin-only (a permanent deletion), gated behind the same "I AGREE" dialog.
    public bool CanPurgeSelected => HasCheckedItems && IsTenantAdmin;
    public string PurgeSelectedLabel => $"Purge selected ({CheckedCount})";

    partial void OnIsTenantAdminChanged(bool value) => OnPropertyChanged(nameof(CanPurgeSelected));

    [RelayCommand]
    private async Task RestoreSelected()
    {
        if (_api is null)
        {
            return;
        }

        var ids = Selection.Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            var (restored, skipped) = await _api.RestoreManyAsync(BinRel("restore-selected"), ids);
            Report(skipped > 0 ? $"Restored {restored} item(s), skipped {skipped}." : $"Restored {restored} item(s).");
            await LoadAsync();
        }
        catch (Exception e)
        {
            Report($"Could not restore the selected items: {e.Message}");
        }
    }

    // Bulk purge of the checked items (ADR "Bulk purge of selected recycle-bin items") — invoked by the code-behind
    // after the "I AGREE" dialog. Tenant-admin-only server-side; protected (legal-hold / WORM) items are skipped.
    public async Task PurgeSelectedAsync()
    {
        if (_api is null)
        {
            return;
        }

        var ids = Selection.Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            var (purged, skipped) = await _api.PurgeManyAsync(BinRel("purge-selected"), ids);
            Report(skipped > 0 ? $"Permanently deleted {purged} item(s), skipped {skipped} (legal hold / locked)." : $"Permanently deleted {purged} item(s).");
            await LoadAsync();
        }
        catch (Exception e)
        {
            Report($"Could not purge the selected items: {e.Message}");
        }
    }

    // Per-row permanent hard-delete — tenant-admin only, no further confirmation (per ADR 0329 request).
    [RelayCommand]
    private async Task HardDelete(RecycleBinRowViewModel? item)
    {
        if (_api is null || item is null)
        {
            return;
        }

        try
        {
            await _api.Documents.PurgeAsync(item.Entry!);
            Report($"Permanently deleted '{item.Name}'.");
            await LoadAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not delete '{item.Name}': {e.Message}");
        }
    }

    // Permanently empties the whole recycle bin — tenant-admin only. Invoked by the code-behind only AFTER the
    // "I AGREE" confirmation dialog (ADR 0329), so there's no extra guard here.
    public async Task HardDeleteAllAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.PurgeRecycleBinAsync(BinRel("purge-all"));
            Report("The recycle bin was permanently emptied.");
            await LoadAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not empty the recycle bin: {e.Message}");
        }
    }
}

// One row in the recycle-bin list: a soft-deleted document with its full path, when it was deleted, and by whom.
public sealed partial class RecycleBinRowViewModel : ObservableObject
{
    // The row the server sent — restore / purge follow the addresses it advertised (ADR 0543/0555). Null only
    // for the designer-preview rows, which reach no server.
    public SimplArchive.DesktopClient.Services.SimplArchiveApiClient.RecycleBinEntry? Entry { get; init; }

    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required DateTimeOffset DeletedAt { get; init; }
    public required string DeletedBy { get; init; }

    /// <summary>Whether the caller may hard-delete — set at row construction; gates the row menu's entry (#530).</summary>
    public bool IsTenantAdmin { get; init; }

    public string DeletedAtText => DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed partial class RecycleBinTabViewModel
{
    // Populates the Recycle bin tab for the headless screenshot (no network) — a couple of deleted rows plus a
    // selected item's read-only detail, so the whole master-detail realizes.
    internal void PopulateDemoForScreenshot()
    {
        IsTenantAdmin = true;
        Items.Clear();
        Items.Add(new RecycleBinRowViewModel { Id = Guid.NewGuid(), Name = "Old draft", Path = "Repositories / Demo Repository / Drafts", DeletedAt = MainWindowViewModel.ScreenshotClock.AddDays(-2), DeletedBy = "Demo Admin" });
        Items.Add(new RecycleBinRowViewModel { Id = Guid.NewGuid(), Name = "Superseded invoice", Path = "Repositories / Demo Repository / Invoices", DeletedAt = MainWindowViewModel.ScreenshotClock.AddHours(-5), DeletedBy = "Demo Admin" });
        OnPropertyChanged(nameof(HasItems));

        DetailTitle = "Old draft";
        SysName = "Old draft";
        SysFileExtension = ".pdf";
        SysDocumentDate = "2026-05-30";
        SysCreated = "2026-05-30 09:14";
        SysCreatedBy = "Demo Admin";
        MaskLine = "Mask: Basic Entry · version 1";
        IndexFields.Add(new IndexFieldViewModel { FieldName = "Keywords", Values = "draft, quarterly" });
        Comments.Add(new ChatMessageViewModel { Id = Guid.Empty, AuthorName = "demo@simplarchive.local", Body = "Replaced by the final version.", CreatedAt = MainWindowViewModel.ScreenshotClock });
        Preview.Reset("Preview renders here (PDF/image/text).");
        SetSelectedForScreenshot(Items[0]);
        Status = string.Format(Strings.Get("StDeletedItems"), Items.Count);
    }

#pragma warning disable MVVMTK0034 // set the backing field so OnSelectedItemChanged doesn't clear the seeded detail
    private void SetSelectedForScreenshot(RecycleBinRowViewModel row)
    {
        _selectedItem = row;
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(HasSelection));
    }
#pragma warning restore MVVMTK0034
}
