using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The My work dashboard (ADR "My work dashboard"): the caller's due reminders and followed documents, and
// opening either of them -- which navigates the workbench to the document behind the row.
//
// The heading covered 201 lines and described 56 of them. The rest was the detail pane's system fields and two
// shell helpers, which is the same tail-decay as tenant settings and bulk actions (#941).
public sealed partial class MainWindowViewModel
{
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
}
