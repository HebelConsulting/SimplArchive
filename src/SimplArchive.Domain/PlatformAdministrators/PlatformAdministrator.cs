namespace SimplArchive.Domain.PlatformAdministrators;

// A principal that exists outside any tenant — deliberately NOT ITenantScoped, no TenantId column at all
// — used solely to authorize tenant-management operations (see ADR "Tenant onboarding and platform-admin
// mechanism"). Authenticates through the same OpenIddict client-credentials flow as ServiceAccount;
// TokenController checks this table when a client_id doesn't match any ServiceAccount. No rights matrix —
// being a genuine, active PlatformAdministrator is itself sufficient for every action that checks it. The
// very first PlatformAdministrator in a deployment still needs direct seeding — a one-time, deployment-
// level bootstrap, not a per-tenant one.
public class PlatformAdministrator
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string OpenIddictApplicationClientId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
