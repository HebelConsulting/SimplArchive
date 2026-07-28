using OpenIddict.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Authentication;

// Runs after app.UseAuthentication()/app.UseAuthorization(): reads the Subject/tenant_id claims off the
// already-validated HttpContext.User and sets the right accessor for the rest of the request pipeline —
// see ADR "ServiceAccount request authentication foundation". A no-op for anonymous requests (e.g.
// /connect/token itself), since there are no claims to read yet. Three-way branch on marker claims — see
// ADR "Tenant onboarding and platform-admin mechanism", ADR "Interactive User login (foundation slice)":
// IsPlatformAdministrator -> only CurrentPlatformAdministratorAccessor (no tenant); IsUser ->
// CurrentUserAccessor + CurrentTenantAccessor (a User does belong to a tenant, unlike a
// PlatformAdministrator); neither -> the original ServiceAccount path (CurrentServiceAccountAccessor +
// CurrentTenantAccessor). Exactly one of the three accessors is ever set per request.
public class CurrentPrincipalMiddleware
{
    private readonly RequestDelegate _next;

    public CurrentPrincipalMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        CurrentTenantAccessor currentTenantAccessor,
        CurrentServiceAccountAccessor currentServiceAccountAccessor,
        CurrentPlatformAdministratorAccessor currentPlatformAdministratorAccessor,
        CurrentUserAccessor currentUserAccessor,
        CurrentImpersonationAccessor currentImpersonationAccessor)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
            var isPlatformAdministrator = context.User.FindFirst(PlatformAdministratorClaimTypes.IsPlatformAdministrator)?.Value == "true";
            var isUser = context.User.FindFirst(UserClaimTypes.IsUser)?.Value == "true";

            if (isPlatformAdministrator)
            {
                if (subject is not null)
                {
                    currentPlatformAdministratorAccessor.PlatformAdministratorId = Guid.Parse(subject);
                }
            }
            else if (isUser)
            {
                var tenantId = context.User.FindFirst(ServiceAccountClaimTypes.TenantId)?.Value;

                if (subject is not null)
                {
                    currentUserAccessor.UserId = Guid.Parse(subject);
                }

                if (tenantId is not null)
                {
                    currentTenantAccessor.TenantId = Guid.Parse(tenantId);
                }

                // Impersonation token (ADR "User impersonation"): the Subject is the target user, and this claim
                // names the acting admin so their actions are attributable + the UI can show a banner.
                var impersonatedBy = context.User.FindFirst(SimplArchive.Auth.ImpersonationConstants.ImpersonatedByClaim)?.Value;
                if (impersonatedBy is not null)
                {
                    currentImpersonationAccessor.ImpersonatorUserId = Guid.Parse(impersonatedBy);
                }
            }
            else
            {
                var tenantId = context.User.FindFirst(ServiceAccountClaimTypes.TenantId)?.Value;

                if (subject is not null)
                {
                    currentServiceAccountAccessor.ServiceAccountId = Guid.Parse(subject);
                }

                if (tenantId is not null)
                {
                    currentTenantAccessor.TenantId = Guid.Parse(tenantId);
                }
            }
        }

        await _next(context);
    }
}
