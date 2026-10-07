using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Api.Provisioning;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// The Licensing Module demo's setup account (ADR 0901) holds exactly what the demo needs: it sees and runs Customers
/// and Packages and may activate modules, and it is not an administrator. The same claims as Flight School's
/// (<see cref="FlightSchoolSetupAccountTests"/>), now that both come from one <see cref="ModuleDemoSetup"/>.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class LicensingSetupAccountTests(E2EApiFactory factory)
{
    [Fact]
    public async Task The_setup_account_runs_customers_and_packages_and_nothing_else()
    {
        var tenantId = await factory.SeedTenantNamedAsync($"lic-setup-{Guid.NewGuid():N}");
        var adminId = await factory.SeedUserAsync(tenantId, $"admin-{Guid.NewGuid():N}@e2e.local", "Passw0rd!Passw0rd!", "Admin", isTenantAdmin: true);
        var clientId = $"licensing-setup-{Guid.NewGuid():N}";
        const string secret = "an-e2e-only-setup-secret-of-sufficient-length";

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LicensingDemo:ServiceAccount:ClientId"] = clientId,
                ["LicensingDemo:ServiceAccount:ClientSecret"] = secret,
                ["LicensingDemo:ServiceAccount:Name"] = "licensing-setup",
            }).Build();

            await LicensingDemoSetup.AddIfConfiguredAsync(scope.ServiceProvider, db, configuration, tenantId, adminId, DateTimeOffset.UtcNow);
        }

        using var api = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));

        var visible = (await TestJson.Get(api, "/api/repositories")).GetProperty("repositories").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).Order().ToList();
        Assert.Equal(["Customers", "Licensing administration", "Packages"], visible);

        // Every right on BOTH repositories, not only on the first one the account helper grants.
        foreach (var slug in new[] { LicensingDemoSetup.CustomersSlug, LicensingDemoSetup.PackagesSlug, LicensingDemoSetup.AdministrationSlug })
        {
            var repository = await TestJson.Get(api, $"/api/documents/{DemoId.For(tenantId, slug)}");
            Assert.True(repository.GetProperty("canManagePermissions").GetBoolean());
        }

        using (var settings = await api.GetAsync("/api/tenant-settings"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        }

        using (var accounts = await api.GetAsync("/api/service-accounts"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, accounts.StatusCode);
        }
    }

    [Fact]
    public async Task Unconfigured_it_creates_nothing()
    {
        var tenantId = await factory.SeedTenantNamedAsync($"lic-none-{Guid.NewGuid():N}");
        var adminId = await factory.SeedUserAsync(tenantId, $"admin-{Guid.NewGuid():N}@e2e.local", "Passw0rd!Passw0rd!", "Admin", isTenantAdmin: true);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        await LicensingDemoSetup.AddIfConfiguredAsync(scope.ServiceProvider, db, new ConfigurationBuilder().Build(), tenantId, adminId, DateTimeOffset.UtcNow);

        Assert.False(await db.Documents.AnyAsync(d => d.Id == DemoId.For(tenantId, LicensingDemoSetup.CustomersSlug)));
        Assert.False(await db.ServiceAccounts.AnyAsync());
    }
}
