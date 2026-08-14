using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using MudBlazor;
using SimplArchive.Localization;

namespace SimplArchive.Client.Services;

/// <summary>
/// Sends the user to sign in when the server repudiates their access token (issue #509).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Every tab wraps its load in a best-effort <c>catch</c> and reports its own
/// message, so a dead session was announced as "Could not load recycle bin" — a different, untrue explanation
/// per tab, none of them mentioning the one thing that would help. The failure is app-wide, so the handling
/// belongs in one place rather than in the eleven components that happen to notice it first.
/// </para>
/// <para>
/// <b>Why the existing catches did not cover it.</b> <see cref="AccessTokenNotAvailableException"/> is thrown
/// when the CLIENT has no token. Here the client has one and the SERVER rejects it: the token is attached
/// without complaint, the response is a plain 401, and the caller sees an <c>HttpRequestException</c> that is
/// indistinguishable from a parse error. Nothing concluded "signed out", so the stale token sat in storage
/// looking valid.
/// </para>
/// <para>
/// <b>Registered outermost</b> on the authorized client, so it observes the final response after the
/// authorization and impersonation handlers have had theirs. It is deliberately NOT on
/// <see cref="ApiRoot.AnonymousClient"/>: that one reads the API root and the theme before anybody signs in
/// (ADR 0578), where a 401 means nothing and a redirect would be a bug.
/// </para>
/// <para>
/// <b>The response is passed through unchanged.</b> Swallowing it would turn every in-flight call into a
/// success carrying no data, which is a worse lie than the one this fixes; callers keep their existing error
/// paths for the moments between the redirect and the teardown.
/// </para>
/// </remarks>
public sealed class SessionExpiredHandler(NavigationManager navigation, ISnackbar snackbar) : DelegatingHandler
{
    // A workbench tab fires several requests at once and every one of them will 401. Without this latch the
    // user gets a burst of navigations instead of one — and the second would discard the return URL captured
    // by the first.
    private bool _redirecting;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && !_redirecting)
        {
            _redirecting = true;

            // Say what happened. A silent bounce to the sign-in page reads as the app having logged you out at
            // random, which is how a user learns to distrust it.
            snackbar.Add(Strings.Get("SessionExpired"), Severity.Warning);

            // NavigateToLogin rather than a plain navigation: it records the return URL in the OIDC state, so
            // signing in again lands where the user was rather than at the workbench root.
            navigation.NavigateToLogin("authentication/login");
        }

        return response;
    }
}
