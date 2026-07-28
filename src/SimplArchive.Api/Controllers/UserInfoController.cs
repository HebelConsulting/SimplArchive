using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The OIDC userinfo endpoint (~/connect/userinfo, passthrough enabled in AddAuthServer). The standard
/// Blazor WASM auth library (oidc-client) defaults to loadUserInfo=true, so it calls this after the code
/// exchange to load the user's claims; without it, sign-in aborts with "There was an error signing in."
/// See ADR "Blazor Client-side login wiring". Authorized against the default scheme (OpenIddict token
/// validation) — the caller presents the access token, whose claims (destined for the AccessToken by
/// AuthorizationController) this echoes back. Stays at root (/connect/*), not under the /api prefix
/// (ADR 0215), like the other OpenIddict endpoints.
/// </summary>
[ApiController]
[Authorize]
public class UserInfoController : ControllerBase
{
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    public IActionResult UserInfo()
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            // Subject is always present on a validated access token — required by the OIDC userinfo spec.
            [Claims.Subject] = User.GetClaim(Claims.Subject)!,
        };

        if (User.GetClaim(Claims.Email) is { Length: > 0 } email)
        {
            claims[Claims.Email] = email;
        }

        return Ok(claims);
    }
}
