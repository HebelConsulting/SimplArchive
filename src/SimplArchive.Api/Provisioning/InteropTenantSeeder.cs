using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Provisioning;

// Env-driven idempotent seed of a SECOND tenant, kept deliberately EMPTY, as the target for external-system
// migration runs (ADR "A seeded migration-target tenant"). Same shape as DemoDataSeeder (ADR 0214) and a no-op
// unless the Interop:* config is present.
//
// Why it exists: the migration tooling authenticates as a machine principal, and a service account's credentials
// are shown ONCE at creation and stored hashed. So every `docker compose down -v` invalidated them and the run
// died at `invalid_client` before it reached anything — the tool's own environment file went stale with the
// volume. Seeding the account with credentials from config makes those values constants: the tooling's config is
// written once and keeps working across any number of recreated stacks.
//
// The tenant stays empty on purpose: it is a migration TARGET, so whatever it holds afterwards is exactly what
// the migration produced, and a count is a result rather than a difference.
public static class InteropTenantSeeder
{
    public static async Task SeedIfConfiguredAsync(IServiceProvider services, IConfiguration configuration)
    {
        var tenantName = configuration["Interop:Tenant:Name"];
        var adminEmail = configuration["Interop:Administrator:Email"];
        var adminPassword = configuration["Interop:Administrator:Password"];

        if (string.IsNullOrWhiteSpace(tenantName)
            || string.IsNullOrWhiteSpace(adminEmail)
            || string.IsNullOrWhiteSpace(adminPassword))
        {
            return;
        }

        var dbContext = services.GetRequiredService<SimplArchiveDbContext>();

        // IgnoreQueryFilters: this runs at startup with no current tenant, exactly like the platform-admin path,
        // so the tenant filter would match nothing and the seed would re-run on every boot.
        if (await dbContext.Tenants.IgnoreQueryFilters()
                .AnyAsync(t => t.Name == tenantName && t.Status == TenantStatus.Active))
        {
            return;
        }

        var provisioned = await services.GetRequiredService<ITenantProvisioningService>().ProvisionAsync(
            tenantName,
            adminEmail,
            configuration["Interop:Administrator:DisplayName"] ?? "Interop Admin",
            configuration["Interop:RepositoryName"] ?? tenantName,
            adminPassword);

        // The tenant's mail domain, declared the way everything else about this tenant is (#667). Optional:
        // an interop stack that does not receive mail simply omits it, and no domain is invented from the
        // administrator's address — guessing one would claim a domain on an operator's behalf.
        await DeclaredMailDomain.EnsureAsync(
            dbContext, provisioned.TenantId, configuration["Interop:MailDomain"], DateTimeOffset.UtcNow,
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InteropTenantSeeder)));

        // No sample tree — see the class comment.
        if (await SeededServiceAccount.AddIfConfiguredAsync(services, dbContext, configuration, "Interop", provisioned))
        {
            await dbContext.SaveChangesAsync();
        }
    }
}
