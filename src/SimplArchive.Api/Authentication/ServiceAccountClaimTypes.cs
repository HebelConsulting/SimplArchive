namespace SimplArchive.Api.Authentication;

// Custom claim type embedded by TokenController and read by CurrentPrincipalMiddleware — see ADR
// "ServiceAccount request authentication foundation". Not an OpenIddict/OAuth standard claim, since
// neither has a predefined tenant-scoping claim.
public static class ServiceAccountClaimTypes
{
    public const string TenantId = "tenant_id";
}
