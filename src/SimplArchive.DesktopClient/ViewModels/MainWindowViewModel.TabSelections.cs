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
}
