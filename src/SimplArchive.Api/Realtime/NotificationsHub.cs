using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SimplArchive.Api.Realtime;

// The real-time notification hub (ADR "Real-time notifications (SignalR)") at /hubs/notifications. Clients only
// receive (server → client "notification" events); there are no client-callable methods. Authenticated — the
// access token arrives via the ?access_token= query string (copied into the Authorization header by middleware,
// since a browser WebSocket handshake can't set headers). Connections are keyed to the User by SubjectUserIdProvider
// so the broadcaster targets Clients.User(userId); a ServiceAccount/PlatformAdministrator connection simply never
// gets pushed to (they have no in-app inbox).
[Authorize]
public sealed class NotificationsHub : Hub
{
}
