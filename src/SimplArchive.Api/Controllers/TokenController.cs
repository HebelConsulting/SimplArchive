using System.Security.Claims;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest extension method
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation;
using SimplArchive.Api.Authentication;
using SimplArchive.Application.Abstractions;
using SimplArchive.Auth;
using SimplArchive.Domain.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Handles /connect/token (passthrough enabled in AddAuthServer — see SimplArchive.Auth.DependencyInjection).
/// Resolves the calling ServiceAccount and embeds its identity/tenant as claims — see ADR "ServiceAccount
/// request authentication foundation". Also resolves PlatformAdministrator when client_id doesn't match
/// any ServiceAccount — see ADR "Tenant onboarding and platform-admin mechanism". Also handles the
/// authorization_code grant for the interactive User login flow — see ADR "Interactive User login
/// (foundation slice)": no new lookup logic needed there, since AuthorizationController already built the
/// identity once at the authorization step and OpenIddict itself persists/validates the code.
/// </summary>
[ApiController]
public class TokenController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IUserSystemRightsResolver _systemRights;
    private readonly CurrentTenantAccessor _tenantAccessor;
    private readonly IAuditRecorder _audit;
    private readonly OpenIddictValidationService _validation;

    public TokenController(
        SimplArchiveDbContext dbContext,
        IUserSystemRightsResolver systemRights,
        CurrentTenantAccessor tenantAccessor,
        IAuditRecorder audit,
        OpenIddictValidationService validation)
    {
        _dbContext = dbContext;
        _systemRights = systemRights;
        _tenantAccessor = tenantAccessor;
        _audit = audit;
        _validation = validation;
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [AllowAnonymous]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType())
        {
            var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            return SignIn(result.Principal!, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.GrantType == ImpersonationConstants.TokenExchangeGrantType)
        {
            return await HandleImpersonationAsync(request);
        }

        if (!request.IsClientCredentialsGrantType())
        {
            return BadRequest(new OpenIddictResponse
            {
                Error = OpenIddictConstants.Errors.UnsupportedGrantType,
            });
        }

        // client_id/client_secret were already validated by OpenIddict's own handlers before this action
        // runs. The tenant isn't known yet at this point, so the lookup ignores the tenant query filter —
        // same pattern already used for cross-tenant group-parent lookups (ADR "Cross-tenant group parent
        // enforcement").
        var serviceAccount = await _dbContext.ServiceAccounts
            .IgnoreQueryFilters()
            .Where(s => s.OpenIddictApplicationClientId == request.ClientId)
            .Select(s => new { s.Id, s.TenantId, s.IsActive })
            .SingleOrDefaultAsync();

        if (serviceAccount is not null)
        {
            if (!serviceAccount.IsActive)
            {
                return BadRequest(new OpenIddictResponse
                {
                    Error = OpenIddictConstants.Errors.InvalidClient,
                });
            }

            var serviceAccountIdentity = new ClaimsIdentity(
                authenticationType: "openiddict",
                nameType: OpenIddictConstants.Claims.Name,
                roleType: OpenIddictConstants.Claims.Role);

            serviceAccountIdentity.SetClaim(OpenIddictConstants.Claims.Subject, serviceAccount.Id.ToString());
            serviceAccountIdentity.SetClaim(ServiceAccountClaimTypes.TenantId, serviceAccount.TenantId.ToString());
            serviceAccountIdentity.SetDestinations(static claim => [OpenIddictConstants.Destinations.AccessToken]);

            return SignIn(new ClaimsPrincipal(serviceAccountIdentity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // PlatformAdministrator isn't ITenantScoped, so no IgnoreQueryFilters() is needed here — no
        // tenant filter ever applies to it. See ADR "Tenant onboarding and platform-admin mechanism".
        var platformAdministrator = await _dbContext.PlatformAdministrators
            .Where(p => p.OpenIddictApplicationClientId == request.ClientId)
            .Select(p => new { p.Id, p.IsActive })
            .SingleOrDefaultAsync();

        if (platformAdministrator is not null)
        {
            if (!platformAdministrator.IsActive)
            {
                return BadRequest(new OpenIddictResponse
                {
                    Error = OpenIddictConstants.Errors.InvalidClient,
                });
            }

            var platformAdministratorIdentity = new ClaimsIdentity(
                authenticationType: "openiddict",
                nameType: OpenIddictConstants.Claims.Name,
                roleType: OpenIddictConstants.Claims.Role);

            platformAdministratorIdentity.SetClaim(OpenIddictConstants.Claims.Subject, platformAdministrator.Id.ToString());
            platformAdministratorIdentity.SetClaim(PlatformAdministratorClaimTypes.IsPlatformAdministrator, "true");
            platformAdministratorIdentity.SetDestinations(static claim => [OpenIddictConstants.Destinations.AccessToken]);

            return SignIn(new ClaimsPrincipal(platformAdministratorIdentity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return BadRequest(new OpenIddictResponse
        {
            Error = OpenIddictConstants.Errors.InvalidClient,
        });
    }

    // RFC 8693 token exchange for User impersonation (ADR "User impersonation"). The acting admin authenticates
    // via their bearer token (Authorization header); `requested_subject` names the target User. Enforces that the
    // actor is an active User holding CanImpersonate and isn't already impersonating, and that the target is an
    // active, same-tenant, non-admin User. Issues a token whose Subject is the target + an `impersonated_by`
    // claim naming the actor, so every subsequent action is attributable.
    private async Task<IActionResult> HandleImpersonationAsync(OpenIddictRequest request)
    {
        var requestedSubject = request.GetParameter(ImpersonationConstants.RequestedSubjectParameter)?.ToString();
        if (!Guid.TryParse(requestedSubject, out var targetUserId))
        {
            return TokenError(OpenIddictConstants.Errors.InvalidRequest, "A 'requested_subject' (target user id) is required.");
        }

        // The actor's own access token (RFC 8693 subject_token) proves who is acting.
        var subjectToken = request.GetParameter(OpenIddictConstants.Parameters.SubjectToken)?.ToString();
        if (string.IsNullOrEmpty(subjectToken))
        {
            return TokenError(OpenIddictConstants.Errors.InvalidRequest, "A 'subject_token' is required.");
        }

        ClaimsPrincipal actorPrincipal;
        try
        {
            actorPrincipal = await _validation.ValidateAccessTokenAsync(subjectToken);
        }
        catch
        {
            return TokenError(OpenIddictConstants.Errors.InvalidGrant, "The subject token is invalid.");
        }

        if (actorPrincipal.GetClaim(UserClaimTypes.IsUser) != "true"
            || actorPrincipal.GetClaim(ImpersonationConstants.ImpersonatedByClaim) is not null
            || !Guid.TryParse(actorPrincipal.GetClaim(OpenIddictConstants.Claims.Subject), out var actorUserId)
            || !Guid.TryParse(actorPrincipal.GetClaim(ServiceAccountClaimTypes.TenantId), out var tenantId))
        {
            return TokenError(OpenIddictConstants.Errors.InvalidGrant, "Impersonation requires an interactive, non-impersonating user token.");
        }

        // Scope the DbContext + rights resolver to the actor's tenant (nothing set it yet on the token endpoint).
        _tenantAccessor.TenantId = tenantId;

        var actor = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == actorUserId);
        if (actor is null || !actor.IsActive)
        {
            return TokenError(OpenIddictConstants.Errors.InvalidGrant, "The acting user is not valid.");
        }

        if (!(await _systemRights.GetEffectiveSystemRightsAsync(actorUserId)).CanImpersonate)
        {
            return TokenError(OpenIddictConstants.Errors.AccessDenied, "You do not have permission to impersonate users.");
        }

        var target = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == targetUserId);
        if (target is null || !target.IsActive || targetUserId == actorUserId)
        {
            return TokenError(OpenIddictConstants.Errors.InvalidGrant, "The impersonation target is not a valid user.");
        }

        var targetRights = await _systemRights.GetEffectiveSystemRightsAsync(targetUserId);
        if (targetRights.IsTenantAdmin || targetRights.CanImpersonate)
        {
            return TokenError(OpenIddictConstants.Errors.InvalidGrant, "An administrator cannot be impersonated.");
        }

        var identity = new ClaimsIdentity("openiddict", OpenIddictConstants.Claims.Name, OpenIddictConstants.Claims.Role);
        identity.SetClaim(OpenIddictConstants.Claims.Subject, targetUserId.ToString());
        identity.SetClaim(ServiceAccountClaimTypes.TenantId, tenantId.ToString());
        identity.SetClaim(UserClaimTypes.IsUser, "true");
        identity.SetClaim(ImpersonationConstants.ImpersonatedByClaim, actorUserId.ToString());
        identity.SetDestinations(static claim => [OpenIddictConstants.Destinations.AccessToken]);

        await _audit.RecordForActorAsync(AuditActorType.User, actorUserId, actor.DisplayName, tenantId,
            AuditActions.ImpersonationStarted, "User", targetUserId, target.DisplayName);

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private BadRequestObjectResult TokenError(string error, string description) => BadRequest(new OpenIddictResponse
    {
        Error = error,
        ErrorDescription = description,
    });
}
