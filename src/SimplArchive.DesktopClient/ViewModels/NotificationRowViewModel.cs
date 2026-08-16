using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// A row in the notifications bell flyout (ADR "Notification viewer + click-through"). IsRead is observable so a
// click can un-bold it in place; DocumentId/DocumentParentId drive click-through navigation.
public sealed partial class NotificationRowViewModel : ObservableObject
{
    public NotificationRowViewModel(NotificationsClient.NotificationInfo n)
    {
        Notification = n;
        Id = n.Id;
        // Digest suffix "(×N)" when this notification coalesced several events (ADR "Notification digest / coalescing").
        Title = n.EventCount > 1 ? $"{n.Title} (×{n.EventCount})" : n.Title;
        Body = n.Body;
        DocumentId = n.DocumentId;
        DocumentParentId = n.DocumentParentId;
        When = n.CreatedAt.LocalDateTime.ToString("g");
        _isRead = n.IsRead;
    }

    // The row the server sent — "mark read" follows its own `read` address (ADR 0543/0555).
    public NotificationsClient.NotificationInfo Notification { get; }

    public Guid Id { get; }
    public string Title { get; }
    public string Body { get; }
    public Guid? DocumentId { get; }
    public Guid? DocumentParentId { get; }
    public string When { get; }

    [ObservableProperty] private bool _isRead;
}
