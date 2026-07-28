namespace SimplArchive.Api.Authentication;

// Marks a client-credentials token as belonging to a PlatformAdministrator rather than a ServiceAccount —
// see ADR "Tenant onboarding and platform-admin mechanism". A platform-admin token carries no
// ServiceAccountClaimTypes.TenantId claim, since PlatformAdministrator isn't scoped to any tenant.
public static class PlatformAdministratorClaimTypes
{
    public const string IsPlatformAdministrator = "is_platform_administrator";
}
