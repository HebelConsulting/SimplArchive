using Microsoft.AspNetCore.SignalR;
using OpenIddict.Abstractions;
using SimplArchive.Api.Authentication;

namespace SimplArchive.Api.Realtime;

// Keys a SignalR connection to its User id (ADR "Real-time notifications (SignalR)") so the broadcaster can target
// Clients.User(userId). Only a User token (the IsUser marker claim) yields a UserIdentifier — the Subject, which
// for a User is User.Id (see CurrentPrincipalMiddleware). A ServiceAccount/PlatformAdministrator connection gets
// no UserIdentifier (null), so it is never targeted — they have no in-app notification intray.
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        var user = connection.User;
        if (user?.FindFirst(UserClaimTypes.IsUser)?.Value != "true")
        {
            return null;
        }

        return user.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
    }
}
