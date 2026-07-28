namespace SimplArchive.Api.Authentication;

// Marks a token as belonging to a logged-in User rather than a ServiceAccount — see ADR "Interactive User
// login (foundation slice)". A User token still carries ServiceAccountClaimTypes.TenantId (a User does
// belong to a tenant, unlike a PlatformAdministrator), just also this marker so CurrentPrincipalMiddleware
// can tell the two apart.
public static class UserClaimTypes
{
    public const string IsUser = "is_user";
}
