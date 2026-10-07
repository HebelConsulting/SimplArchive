namespace SimplArchive.Api.Provisioning;

/// <summary>
/// The Licensing Module demo's repositories and its unattended setup principal (ADR 0901), the same shape as Flight
/// School's (ADR 0894): <i>Customers</i>, <i>Packages</i> and an Administration repository for the module's own licence, and an account that may activate modules and holds every
/// right on those two repositories and nothing else. The module's demo seed then assigns the register masks, files the
/// licence, sets the signing key and grants the module's principal, all through <c>saconsole</c> and followed rels.
/// </summary>
/// <remarks>A no-op unless <c>LicensingDemo:ServiceAccount:ClientId</c>/<c>ClientSecret</c> are configured, which only
/// the module's local demo does.</remarks>
public static class LicensingDemoSetup
{
    /// <summary>The slugs the demo seed derives the same UUIDv5 ids from (#781).</summary>
    public const string CustomersSlug = "repository/licensing-customers";
    public const string PackagesSlug = "repository/licensing-packages";

    /// <summary>Where the module's own licence is filed: Customers and Packages admit only their own kinds.</summary>
    public const string AdministrationSlug = "repository/licensing-administration";

    public static Task AddIfConfiguredAsync(
        IServiceProvider services, SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext dbContext, IConfiguration configuration,
        Guid tenantId, Guid adminId, DateTimeOffset now) =>
        ModuleDemoSetup.AddIfConfiguredAsync(services, dbContext, configuration, "LicensingDemo", tenantId, adminId, now,
            [
                new ModuleDemoSetup.Repository(CustomersSlug, "Customers"),
                new ModuleDemoSetup.Repository(PackagesSlug, "Packages"),
                new ModuleDemoSetup.Repository(AdministrationSlug, "Licensing administration"),
            ],
            account => account.CanManageModules = true);
}
