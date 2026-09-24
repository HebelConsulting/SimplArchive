using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest extension method
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using SimplArchive.Api.Authentication;
using SimplArchive.Api.Controllers;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Pages.Account;

/// <summary>
/// The human half of the device authorization grant (RFC 8628, ADR 0823) — served at
/// <c>/connect/verify</c>, the end-user verification endpoint whose passthrough is the ONLY one
/// <c>AddAuthServer</c> enables. saconsole prints a code on a machine with no browser; this is where a
/// person, in a browser they already trust, says whether that machine may act as them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Re-authentication is mandatory, and that is a decision rather than an accident.</b> The conventional
/// device-flow page approves on one click when a session already exists. This one does not: the grant mints
/// a token for a DIFFERENT machine than the browser, and saconsole's verbs are tenant creation and token
/// initialisation — so an unlocked laptop plus a code read aloud would otherwise be enough. An administrator
/// whose cookie is older than <see cref="MaxSessionAge"/> is signed out and sent through the sign-in page,
/// which is exactly the shape <c>AuthorizationController</c> already uses for <c>prompt=login</c>.
/// </para>
/// <para>
/// <b>What makes "every time" literal is a signed marker, not a time window.</b> The redirect to the sign-in
/// page carries a data-protected token recording the moment this page signed the session out; coming back,
/// the cookie's own <see cref="AuthenticationProperties.IssuedUtc"/> must be at or after that moment. Only a
/// real sign-in can arrange that. A plain "was the cookie issued in the last few minutes?" window was the
/// obvious first shape and is weaker on both counts: it accepts a session begun moments earlier for an
/// unrelated reason, and nothing can test it without faking a clock.
/// </para>
/// <para>
/// The marker is deliberately NOT a plain query flag — a flag in a URL is set by whoever wrote the link,
/// which is precisely the person this exists to stop. It is also not a licence to trust the code that comes
/// with it: someone can mint a marker for THEIR code and send the link on, so the approver is shown the code
/// and asked to compare it with their own terminal before approving (RFC 8628 §5.4). The echo is the defence
/// there; the marker only guarantees that whoever answers has just proved who they are.
/// </para>
/// <para>
/// A code that cannot be used — expired, already redeemed, never issued, mistyped — gets ONE message. The
/// remedy is identical in every case (start again in the terminal), and distinguishing them would confirm to
/// a guesser which codes are real.
/// </para>
/// </remarks>
[AllowAnonymous]
public class DeviceModel : PageModel
{
    /// <summary>
    /// How long the sign-out marker stays usable — a bound on how stale a half-finished approval may be, and
    /// deliberately shorter than a user code's own life so an abandoned link expires before the code does.
    /// A constant rather than a setting: it is one half of the security decision above, and an operator who
    /// could widen it would have weakened re-authentication without knowing they had.
    /// </summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The query/form parameter carrying the sign-out marker back from the sign-in page.</summary>
    public const string ChallengeParameter = "c";

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IAuditRecorder _audit;
    private readonly ITimeLimitedDataProtector _challengeProtector;
    private readonly ILogger<DeviceModel> _logger;

    public DeviceModel(
        SimplArchiveDbContext dbContext,
        IOpenIddictApplicationManager applications,
        IAuditRecorder audit,
        IDataProtectionProvider dataProtection,
        ILogger<DeviceModel> logger)
    {
        _dbContext = dbContext;
        _applications = applications;
        _audit = audit;
        _challengeProtector = dataProtection.CreateProtector("SimplArchive.DeviceVerification").ToTimeLimitedDataProtector();
        _logger = logger;
    }

    /// <summary>What the page is showing: the code entry form, the approval, or the one refusal message.</summary>
    public enum Step
    {
        EnterCode,
        Approve,
        Unusable,
    }

    public Step State { get; private set; } = Step.EnterCode;

    /// <summary>The code as the administrator's terminal printed it — echoed so the two can be compared.</summary>
    public string? UserCode { get; private set; }

    /// <summary>The requesting client's display name, from its own registration.</summary>
    public string? ClientName { get; private set; }

    /// <summary>The account the token would represent.</summary>
    public string? Account { get; private set; }

    /// <summary>
    /// The sign-out marker this request arrived with, echoed into the forms so the approval POST carries the
    /// same proof the GET did. Without it the POST would re-challenge and the click would be lost.
    /// </summary>
    public string? Marker { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        var code = request?.UserCode;

        if (await RequireReauthenticationAsync(code) is { } challenge)
        {
            return challenge;
        }

        Marker = Request.Query[ChallengeParameter].ToString();

        // No code yet — somebody opened the bare verification URI rather than the complete one saconsole
        // prints. RFC 8628 §3.3.1 expects a form here, so this is the normal path for a typed code, not an
        // error.
        if (string.IsNullOrWhiteSpace(code))
        {
            State = Step.EnterCode;
            return Page();
        }

        return await ShowApprovalAsync(code);
    }

    /// <summary>The typed-code form: re-enter the verification endpoint with the code as a parameter.</summary>
    /// <remarks>
    /// The marker rides along, so typing a code is not itself a reason to sign in again — the administrator
    /// has just done so, and sending them back would be a loop they cannot get out of by typing correctly.
    /// </remarks>
    public IActionResult OnPostCode(string? userCode, string? c)
    {
        var query = new List<string>();

        if (userCode?.Trim() is { Length: > 0 } trimmed)
        {
            query.Add($"user_code={Uri.EscapeDataString(trimmed)}");
        }

        if (!string.IsNullOrEmpty(c))
        {
            query.Add($"{ChallengeParameter}={Uri.EscapeDataString(c)}");
        }

        return Redirect(query.Count == 0 ? "/connect/verify" : $"/connect/verify?{string.Join('&', query)}");
    }

    public async Task<IActionResult> OnPostApproveAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        var code = request?.UserCode;

        if (await RequireReauthenticationAsync(code) is { } challenge)
        {
            return challenge;
        }

        var session = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (string.IsNullOrWhiteSpace(code) || await ResolveUserCodeAsync() is not { } pending)
        {
            Marker = Request.Form[ChallengeParameter].ToString();
            State = Step.Unusable;
            return Page();
        }

        var userId = Guid.Parse(session.Principal!.FindFirst(OpenIddictConstants.Claims.Subject)!.Value);

        // IgnoreQueryFilters(["TenantFilter"]) — the cookie carries only Subject, so the tenant is not known
        // until this lookup resolves it. Same reasoning as AuthorizationController's.
        var user = await _dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.Id == userId)
            .Select(u => new { u.TenantId, u.Email, u.DisplayName, u.IsActive })
            .SingleOrDefaultAsync();

        if (user is null || !user.IsActive)
        {
            // Deactivated between signing in and clicking Approve. Refusing through the OpenIddict scheme
            // rather than just rendering an error matters: it tells the waiting terminal that the answer is
            // no, instead of leaving it polling until the code expires.
            return RefuseThroughOpenIddict();
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

        // The scopes come from the pending request, never from this form: the client asked for them at the
        // device-authorization endpoint, and the person's job here is to allow or refuse that ask — not to
        // widen it.
        principal.SetScopes(pending.GetScopes());

        principal.SetDestinations(claim => claim.Type switch
        {
            OpenIddictConstants.Claims.Subject or OpenIddictConstants.Claims.Email =>
                [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
            _ => [OpenIddictConstants.Destinations.AccessToken],
        });

        var clientId = pending.GetClaim(OpenIddictConstants.Claims.ClientId);

        // Approving hands a token — and, with offline_access, a renewable one — to a machine the approver may
        // not be sitting at. That is the same class of act as issuing a WebDAV password, and is recorded for
        // the same reason: an auditor reconstructing "what could act as this person" needs it in the timeline.
        await _audit.RecordForActorAsync(
            AuditActorType.User, userId, user.DisplayName, user.TenantId,
            AuditActions.DeviceGrantApproved, targetName: clientId);

        _logger.LogInformation(
            "User {UserId} approved a device grant for client {ClientId} in tenant {TenantId}",
            userId, clientId, user.TenantId);

        // RedirectUri is how the result page gets shown at all: ProcessHostRedirectionResponse is registered
        // for ApplyEndUserVerificationResponseContext and nothing else, so this endpoint is the one place
        // OpenIddict honours a host-supplied redirect after a sign-in. Without it the administrator's reward
        // for approving is a blank page.
        return SignIn(
            principal,
            new AuthenticationProperties { RedirectUri = $"/Account/DeviceResult?outcome={DeviceResultModel.Approved}" },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    public async Task<IActionResult> OnPostRefuseAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest();

        if (await RequireReauthenticationAsync(request?.UserCode) is { } challenge)
        {
            return challenge;
        }

        var session = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var clientId = (await ResolveUserCodeAsync())?.GetClaim(OpenIddictConstants.Claims.ClientId);

        if (session.Principal?.FindFirst(OpenIddictConstants.Claims.Subject)?.Value is { } subject
            && Guid.TryParse(subject, out var userId))
        {
            var user = await _dbContext.Users
                .IgnoreQueryFilters(["TenantFilter"])
                .Where(u => u.Id == userId)
                .Select(u => new { u.TenantId, u.DisplayName })
                .SingleOrDefaultAsync();

            if (user is not null)
            {
                // A refusal is recorded as well as an approval, and the asymmetry would be the bug: a token
                // request somebody said NO to is exactly what a SIEM correlating "who tried" wants to see.
                await _audit.RecordForActorAsync(
                    AuditActorType.User, userId, user.DisplayName, user.TenantId,
                    AuditActions.DeviceGrantRefused, targetName: clientId);

                _logger.LogInformation(
                    "User {UserId} refused a device grant for client {ClientId} in tenant {TenantId}",
                    userId, clientId, user.TenantId);
            }
        }

        return RefuseThroughOpenIddict();
    }

    /// <summary>
    /// Tells the waiting terminal the answer is <c>access_denied</c>, and sends the browser to the page that
    /// says so. Both halves matter: without the first the tool keeps polling a code nobody will ever approve.
    /// </summary>
    private IActionResult RefuseThroughOpenIddict() => Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.AccessDenied,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                "The device authorization was refused by the end user.",
        })
        {
            RedirectUri = $"/Account/DeviceResult?outcome={DeviceResultModel.Refused}",
        },
        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    /// <summary>
    /// Null when this browser has authenticated since the marker it presents was minted — the one thing that
    /// counts as having re-authenticated for this approval. Otherwise the redirect that makes it happen: the
    /// existing session is signed OUT first, because leaving it would let the very next request satisfy the
    /// prompt this one just demanded.
    /// </summary>
    private async Task<IActionResult?> RequireReauthenticationAsync(string? userCode)
    {
        var session = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (session.Succeeded
            && session.Principal?.FindFirst(OpenIddictConstants.Claims.Subject) is not null
            && session.Properties?.IssuedUtc is { } issued
            && SignedOutAt(Request.Query[ChallengeParameter].ToString(), Request.HasFormContentType ? Request.Form[ChallengeParameter].ToString() : null) is { } marker
            && issued >= marker)
        {
            return null;
        }

        if (session.Succeeded)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        var target = string.IsNullOrWhiteSpace(userCode)
            ? $"/connect/verify?{ChallengeParameter}={Uri.EscapeDataString(MintMarker())}"
            : $"/connect/verify?user_code={Uri.EscapeDataString(userCode)}&{ChallengeParameter}={Uri.EscapeDataString(MintMarker())}";

        return LocalRedirect($"/Account/Login?ReturnUrl={Uri.EscapeDataString(target)}");
    }

    /// <summary>
    /// The principal a presented user code resolves to, or null when the code is not usable — expired,
    /// already redeemed, never issued, or mistyped.
    /// </summary>
    /// <remarks>
    /// <b>Do not shorten this to <c>result.Succeeded</c>.</b> At this endpoint OpenIddict answers SUCCESS
    /// with a principal even when no user code resolved, so a `Succeeded` test renders the approval page for
    /// any string at all: <c>?user_code=ZZZZ-ZZZZ</c> produced a complete, plausible approval card with an
    /// empty "Requested by" and a live Approve button. The code itself is echoed from the query, so it looked
    /// exactly like a real request. What actually distinguishes the two is the USER CODE PRINCIPAL that
    /// OpenIddict attaches to the result's parameters, so that is what is asked for — and the client id is
    /// required with it, because a principal carrying no client is not an authorization anybody made.
    /// </remarks>
    private async Task<ClaimsPrincipal?> ResolveUserCodeAsync()
    {
        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        var principal = result.Properties?.GetParameter<ClaimsPrincipal>(
            OpenIddictServerAspNetCoreConstants.Properties.UserCodePrincipal) ?? result.Principal;

        return principal?.GetClaim(OpenIddictConstants.Claims.ClientId) is not null ? principal : null;
    }

    /// <summary>The marker this page hands to the sign-in page: the instant it signed the old session out.</summary>
    private string MintMarker() => _challengeProtector.Protect(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ChallengeLifetime);

    /// <summary>
    /// The instant a presented marker records, or null when there is none, it has expired, or it was not
    /// minted by this application. A tampered or expired marker is not an error worth distinguishing — both
    /// mean "sign in again", which is what the caller does with a null.
    /// </summary>
    private DateTimeOffset? SignedOutAt(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(
                    long.Parse(_challengeProtector.Unprotect(candidate), CultureInfo.InvariantCulture));
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException or OverflowException)
            {
                // Expired, tampered with, or minted by a different key ring. Fall through to the next
                // candidate, and to the sign-in redirect if there is none.
            }
        }

        return null;
    }

    private async Task<IActionResult> ShowApprovalAsync(string code)
    {
        if (await ResolveUserCodeAsync() is not { } pending)
        {
            State = Step.Unusable;
            return Page();
        }

        UserCode = code;
        State = Step.Approve;

        var clientId = pending.GetClaim(OpenIddictConstants.Claims.ClientId)!;
        ClientName = await _applications.FindByClientIdAsync(clientId) is { } application
            ? await _applications.GetDisplayNameAsync(application) ?? clientId
            : clientId;

        var session = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var userId = Guid.Parse(session.Principal!.FindFirst(OpenIddictConstants.Claims.Subject)!.Value);

        Account = await _dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .SingleOrDefaultAsync();

        return Page();
    }
}
