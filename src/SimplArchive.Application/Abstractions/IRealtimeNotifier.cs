namespace SimplArchive.Application.Abstractions;

// Pushes a just-written in-app notification to a user's live connections (ADR "Real-time notifications
// (SignalR)"). Called from the single DbContext SaveChanges choke point after a Notification row commits, so
// every write path (INotificationService + the background sweeps) is covered. The default implementation is a
// no-op (NullRealtimeNotifier); the Api overrides it with a SignalR hub-context broadcaster. Best-effort —
// callers never let a push failure break the mutation.
public interface IRealtimeNotifier
{
    Task NotifyUserAsync(Guid userId, RealtimeNotification notification, CancellationToken cancellationToken = default);
}

// The minimal payload pushed over the wire — enough for a live toast. The client re-fetches the authoritative
// bell list/count (which carries documentParentId for click-through) on receiving one.
public sealed record RealtimeNotification(string Title, string Body);
