using Microsoft.AspNetCore.SignalR;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Realtime;

// The SignalR implementation of IRealtimeNotifier (ADR "Real-time notifications (SignalR)") — pushes a notification
// to a user's live connections via the hub context. Registered in Program.cs to override the default
// NullRealtimeNotifier, so the DbContext choke point delivers live. In-process only this slice — a multi-instance
// deployment needs a Redis/Valkey backplane (ADR 0017), deferred.
public sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<NotificationsHub> _hub;

    public SignalRRealtimeNotifier(IHubContext<NotificationsHub> hub) => _hub = hub;

    public Task NotifyUserAsync(Guid userId, RealtimeNotification notification, CancellationToken cancellationToken = default) =>
        _hub.Clients.User(userId.ToString()).SendAsync("notification", notification, cancellationToken);
}
