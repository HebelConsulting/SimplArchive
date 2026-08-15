namespace SimplArchive.DesktopClient.ViewModels;

// The #530 tab selections + their screenshot populate, split out on arrival: MainWindowViewModel is on the
// 1000-line standing-debt list and its ceiling only shrinks — a new concern takes a home of its own instead
// of paying for its lines with a raised ceiling (the same rule that split the window code-behind, #466).
public sealed partial class MainWindowViewModel
{
    /// <summary>The task row the ribbon's Open acts on (#530, tranche 3).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasSelectedTaskRow))]
    private TaskItemViewModel? _selectedTaskRow;

    public bool HasSelectedTaskRow => SelectedTaskRow is not null;

    /// <summary>The retention row the ribbon acts on (#530, tranche 2) — single-select by decision.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasSelectedRetentionRow))]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(SelectedRetentionCanDispose))]
    private RetentionRowViewModel? _selectedRetentionRow;

    public bool HasSelectedRetentionRow => SelectedRetentionRow is not null;

    public bool SelectedRetentionCanDispose => SelectedRetentionRow?.CanDispose == true;

    // Populates the Retention tab for the headless screenshot (#530 tranche 2): one row per status, so the
    // render proves the row TEMPLATE, which no VM test can see.
    internal void PopulateRetentionDemoForScreenshot()
    {
        IsLoggedIn = true;
        RetentionItems.Clear();
        RetentionItems.Add(new RetentionRowViewModel(Guid.NewGuid(), "Framework agreement 2019", 7, "2026-05-01", true, false, null, null!));
        RetentionItems.Add(new RetentionRowViewModel(Guid.NewGuid(), "Invoice 2026-003", 7, "2033-01-14", false, false, null, null!));
        RetentionItems.Add(new RetentionRowViewModel(Guid.NewGuid(), "Disputed delivery note", 7, "2026-04-01", true, true, null, null!));
        SelectedRetentionRow = RetentionItems[0];
    }

    // Populates the Legal holds tab for the headless screenshot (#530 tranche 5): one active hold with items
    // (selected, so the detail + the ✕ rows render) and one released, so the render proves both row states.
    internal void PopulateLegalHoldsDemoForScreenshot()
    {
        IsLoggedIn = true;
        LegalHolds.Clear();
        SelectedHoldItems.Clear();
        var items = new List<Services.SimplArchiveApiClient.LegalHoldItemInfo>
        {
            new(Guid.NewGuid(), "Disputed delivery note"),
            new(Guid.NewGuid(), "Framework agreement 2019"),
        };
        var active = new Services.SimplArchiveApiClient.LegalHoldInfo(
            Guid.NewGuid(), "Case 2026-17 Meyer", "Pending litigation", new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero), true, items.Count, items);
        var released = new Services.SimplArchiveApiClient.LegalHoldInfo(
            Guid.NewGuid(), "Audit 2025", null, new DateTimeOffset(2025, 11, 5, 14, 0, 0, TimeSpan.Zero), false, 0, []);
        LegalHolds.Add(new LegalHoldRowViewModel(active.Id, active.Name, active.IsActive, active.ItemCount, active));
        LegalHolds.Add(new LegalHoldRowViewModel(released.Id, released.Name, released.IsActive, released.ItemCount, released));
        SelectedLegalHold = LegalHolds[0];
        foreach (var item in items)
        {
            SelectedHoldItems.Add(new LegalHoldItemRowViewModel(item.DocumentId, item.DocumentName, item));
        }
    }

    /// <summary>The catalog tag the ribbon acts on (#530, tranche 6) — single-select.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasSelectedTagRow))]
    private TagCatalogRow? _selectedTagRow;

    public bool HasSelectedTagRow => SelectedTagRow is not null;

    /// <summary>The ribbon's refresh — the load itself lives with the other tag commands.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task RefreshTagCatalog() => LoadTagCatalogAsync();

    // Populates the Tag catalog for the headless screenshot (#530 tranche 6): coloured, colourless and
    // selected rows, so the render proves the row template + the ribbon's greying.
    internal void PopulateTagsDemoForScreenshot()
    {
        IsLoggedIn = true;
        IsTenantAdmin = true;
        TagCatalogAdmin.Clear();
        TagCatalogAdmin.Add(new TagCatalogRow(new Services.SimplArchiveApiClient.TagCatalogItem(Guid.NewGuid(), "contract", "#2e7d32")));
        TagCatalogAdmin.Add(new TagCatalogRow(new Services.SimplArchiveApiClient.TagCatalogItem(Guid.NewGuid(), "invoice", "#1565c0")));
        TagCatalogAdmin.Add(new TagCatalogRow(new Services.SimplArchiveApiClient.TagCatalogItem(Guid.NewGuid(), "urgent", null)));
        SelectedTagRow = TagCatalogAdmin[0];
    }
}
