using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Notifications;

// The default no-op real-time notifier (ADR "Real-time notifications (SignalR)") — used when SignalR isn't wired
// (tests, non-Api hosts). The Api overrides this registration with a SignalR hub-context broadcaster.
public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task NotifyUserAsync(Guid userId, RealtimeNotification notification, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
