using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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
public class LogoutModel : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // LOCAL ONLY. A returnUrl is caller-supplied, and honouring an absolute one would make this an open
        // redirect on an endpoint every user is sent to by name — the classic phishing hand-off.
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
