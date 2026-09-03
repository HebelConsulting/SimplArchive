using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The in-app notifications bell (ADR "Notification viewer + click-through"): the list, the unread badge,
// marking everything read, opening one -- and the REALTIME connection that keeps the badge current, whose
// start and stop belong here because nothing else observes it.
public sealed partial class MainWindowViewModel
{
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
}
