using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using SimplArchive.Api.Controllers;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Pages.Account;

/// <summary>Ends the interactive session: signs the interim cookie out, then returns to the app.</summary>
/// <remarks>
/// <para>
/// Logging out of the web client used to clear only the CLIENT'S tokens. The cookie this page signs out is the
/// one <c>/connect/authorize</c> authenticates against, so it survived — and the SPA's silent sign-in
/// (<c>prompt=none</c>) then restored the whole session on the next page load. The screen said "You are logged
/// out" while the app bar still named the user, and a plain reload put them back in the workbench.
/// </para>
/// <para>
/// That was not only confusing. The cookie also authorizes <c>/Account/Passkeys</c>, so whoever used the browser
/// next could enrol their own passkey against the account — turning a session somebody believed they had ended
/// into durable credentials. It is a session cookie, so closing the browser ended it; but "log out" exists
/// precisely for handing a still-open browser to someone else, and the kiosk is a public demo whose browser
/// stays open all day.
/// </para>
/// <para>
/// Supersedes ADR 0334's "no end-session endpoint". This is deliberately NOT an OIDC <c>end_session</c>
/// endpoint with the full RP-initiated-logout contract — no <c>id_token_hint</c>, no client-registered
/// post-logout redirect list. It is a local sign-out serving this app's own clients, which is why the return is
/// restricted to a local URL and never to a caller-supplied host.
/// </para>
/// </remarks>
public class LogoutModel(
    IAuditRecorder audit,
    SimplArchiveDbContext dbContext,
    IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictTokenManager tokens) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        // BEFORE the sign-out, because the cookie is what names the actor — afterwards there is nobody to
        // attribute it to. ADR 0065 puts logout in audit scope, and without it every sign-in in the trail is
        // open-ended: "was anyone still signed in when this happened" becomes a guess (#847, A07/A09).
        await RecordSignOutAsync();

        // And the tokens, also while the cookie still names whose they are (#1578, ADR 0895).
        await RevokeWebSessionsAsync();

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // LOCAL ONLY. A returnUrl is caller-supplied, and honouring an absolute one would make this an open
        // redirect on an endpoint every user is sent to by name — the classic phishing hand-off.
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    /// <summary>
    /// Revokes this user's sessions of the WEB client — its authorizations and every token issued under them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Signing out used to end the cookie and the client's copy while the tokens themselves stayed valid: an access
    /// token in somebody else's hands kept working for up to 15 minutes, a refresh token for 30 days. With the API
    /// now checking each request's token against its stored entry, revoking here takes effect on the next request.
    /// </para>
    /// <para>
    /// <b>That client's sessions, not every session</b> (owner, 2026-10-05). This page is the WEB client's
    /// sign-out, so it revokes what was issued to <c>blazor-client</c> for this user — every browser they are signed
    /// in with — and leaves the desktop and saconsole alone. The desktop ends its own through the revocation
    /// endpoint. There is no bearer token to revoke "the one being ended" by: this is a plain browser GET.
    /// </para>
    /// </remarks>
    private async Task RevokeWebSessionsAsync()
    {
        var session = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (session.Principal?.FindFirst(OpenIddictConstants.Claims.Subject)?.Value is not { } subject
            || await applications.FindByClientIdAsync("blazor-client", HttpContext.RequestAborted) is not { } web
            || await applications.GetIdAsync(web, HttpContext.RequestAborted) is not { } webId)
        {
            return;
        }

        await authorizations.RevokeAsync(subject, webId, status: null, type: null, HttpContext.RequestAborted);
        await tokens.RevokeAsync(subject, webId, status: null, type: null, HttpContext.RequestAborted);
    }

    private async Task RecordSignOutAsync()
    {
        // THE COOKIE SCHEME EXPLICITLY, not HttpContext.User. The default authentication scheme is
        // OpenIddict's validation handler (it has to be, or a bare [Authorize] throws instead of answering
        // 401), so `User` on this page is empty even for somebody who is very much signed in — and the audit
        // event would silently never be written. The cookie signed out two lines below is the one that names
        // the actor, so it is the one asked.
        var session = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // An anonymous hit is ordinary — a bookmark, a second tab, a client clearing up after a session that
        // already ended — and there is nothing to record for it. Signing out is idempotent, so the page still
        // answers; only the audit event is skipped.
        if (session.Principal?.FindFirst(OpenIddictConstants.Claims.Subject)?.Value is not { } subject
            || !Guid.TryParse(subject, out var userId))
        {
            return;
        }

        // The tenant filter has no ambient tenant on this page — the same reason every pre-session lookup in
        // the auth surface ignores it (ADR 0150's note on tenant-less lookups).
        var user = await dbContext.Users.IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(u => u.Id == userId, HttpContext.RequestAborted);
        if (user is null)
        {
            return;
        }

        await audit.RecordForActorAsync(
            AuditActorType.User, user.Id, user.DisplayName, user.TenantId, AuditActions.LoggedOut);
    }
}
