using System.Security.Claims;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest extension method
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using SimplArchive.Api.Authentication;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Handles ~/connect/authorize (passthrough enabled in AddAuthServer). See ADR "Interactive User login
/// (foundation slice)": authenticates against the cookie scheme (set by Pages/Account/Login.cshtml, not
/// the default OpenIddict validation scheme used for bearer-token Api calls); if absent, redirects to the
/// login page (explicitly, not via Challenge() — ASP.NET Core's cookie handler has built-in "is this an
/// AJAX request" heuristics that return a bare 401 instead of redirecting for some request shapes, which
/// isn't what a browser-driven authorization request needs here). Once authenticated, builds the same
/// claims shape TokenController uses for a ServiceAccount token (Subject, tenant_id), plus the User
/// marker claim, and signs in against OpenIddict's own scheme — which is what actually issues the
/// authorization code. Also propagates the requested scopes and issues an identity token (Subject/Email
/// claims) when "openid" was requested — see ADR "Blazor Client-side login wiring", which needed real OIDC
/// (not just an access token) for the standard Blazor WASM auth library to build a ClaimsPrincipal.
/// </summary>
[ApiController]
public class AuthorizationController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;

    public AuthorizationController(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // prompt=login forces re-authentication even when a cookie session already exists — the desktop client
        // uses it for "log out, then log in as a different user" (ADR "Desktop logout / switch user"); without
        // it the browser's cookie would silently SSO the same user, so a new tenant/user could never be reached.
        // Sign the interim cookie out and send to the login page, stripping prompt=login from the ReturnUrl so
        // the post-login re-authorize doesn't sign out again in a loop.
        if (result.Succeeded && request.HasPromptValue(OpenIddictConstants.PromptValues.Login))
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return LocalRedirect($"/Account/Login?ReturnUrl={Uri.EscapeDataString(ReturnUrlWithoutPrompt())}");
        }

        if (!result.Succeeded || result.Principal is null)
        {
            // prompt=none means "authenticate without any interactive UI, or fail" — the Blazor auth
            // library uses it for the silent sign-in / access-token acquisition it fires from a hidden
            // iframe on load. Redirecting that iframe to the HTML login page (as the interactive branch
            // below does) breaks it; the correct response is the standard OIDC login_required error, which
            // OpenIddict delivers back to the client via the redirect_uri so the library cleanly concludes
            // "no existing session." See ADR "Blazor Client-side login wiring".
            if (request.HasPromptValue(OpenIddictConstants.PromptValues.None))
            {
                return Forbid(
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.LoginRequired,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user is not logged in.",
                    }),
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            // prompt=login is STRIPPED here too, and that is the fix for a double login (#754's neighbour).
            //
            // The branch above strips it when a cookie already exists. This one runs when there is NONE — a
            // fresh browser, or one that has just been signed out — and it used to carry the whole query
            // through, prompt included. So the user authenticated, the re-authorize arrived with a valid
            // cookie AND prompt=login, took the branch above, signed that brand-new cookie straight out and
            // asked for credentials a second time. Sending them to the login page IS the re-authentication
            // prompt=login asks for; once they are on it, the demand has been met.
            return LocalRedirect($"/Account/Login?ReturnUrl={Uri.EscapeDataString(ReturnUrlWithoutPrompt())}");
        }

        var userId = Guid.Parse(result.Principal.FindFirst(OpenIddictConstants.Claims.Subject)!.Value);

        // IgnoreQueryFilters(["TenantFilter"]) — the tenant isn't known yet at this point either (the
        // interim cookie only carries Subject, not tenant_id), same reasoning as Login.cshtml.cs's own
        // fix and TokenController's ServiceAccount lookup.
        var user = await _dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.Id == userId)
            .Select(u => new { u.TenantId, u.Email, u.IsActive })
            .SingleOrDefaultAsync();

        if (user is null || !user.IsActive)
        {
            return Forbid(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        var identity = new ClaimsIdentity(
            authenticationType: "openiddict",
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.SetClaim(OpenIddictConstants.Claims.Subject, userId.ToString());
        identity.SetClaim(OpenIddictConstants.Claims.Email, user.Email);
        identity.SetClaim(ServiceAccountClaimTypes.TenantId, user.TenantId.ToString());
        identity.SetClaim(UserClaimTypes.IsUser, "true");

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());

        // Subject/Email also destined for the identity token — everything else (tenant_id, the User
        // marker) stays access-token-only, keeping the id_token to the minimum OIDC needs.
        principal.SetDestinations(claim => claim.Type switch
        {
            OpenIddictConstants.Claims.Subject or OpenIddictConstants.Claims.Email =>
                [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
            _ => [OpenIddictConstants.Destinations.AccessToken],
        });

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>This authorize request's URL with <c>prompt</c> removed, for a redirect to the login page.</summary>
    /// <remarks>
    /// Both redirects to <c>/Account/Login</c> use it, and they must: sending the user to the login page IS the
    /// re-authentication that <c>prompt=login</c> demands, so carrying the parameter past that point asks for it
    /// a second time. Leaving it on ONE of the two paths produced exactly that — a user who signed in, and was
    /// immediately shown the sign-in form again, on any browser that did not already hold a cookie.
    /// </remarks>
    private string ReturnUrlWithoutPrompt()
    {
        var query = QueryHelpers.ParseQuery(Request.QueryString.Value ?? string.Empty);
        query.Remove("prompt");
        return Request.PathBase + Request.Path + QueryString.Create(
            query.SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v))));
    }
}
