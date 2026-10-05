using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Api.Provisioning;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// The Flight School demo's unattended setup account (FlightSchool#85) holds exactly what the demo needs — and an
/// administrator's reach it does not.
/// </summary>
/// <remarks>
/// The demo used to be seeded as the tenant administrator, password on the command line. Its replacement is a
/// seeded service account the owner chose to make broader than the Crypto tenant's (it creates pilot USERS and
/// isolates their dossiers) while staying far short of an administrator. Both halves are claims about rights, so
/// both are asserted against the real API with the account's own token: what it can do, and what it cannot.
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class FlightSchoolSetupAccountTests(E2EApiFactory factory)
{
    [Fact]
    public async Task The_setup_account_runs_the_school_and_nothing_else()
    {
        var tenantId = await factory.SeedTenantNamedAsync($"fs-setup-{Guid.NewGuid():N}");
        var adminId = await factory.SeedUserAsync(tenantId, $"admin-{Guid.NewGuid():N}@e2e.local", "Passw0rd!Passw0rd!", "Admin", isTenantAdmin: true);
        var clientId = $"flight-school-setup-{Guid.NewGuid():N}";
        const string secret = "an-e2e-only-setup-secret-of-sufficient-length";

        Guid otherRepositoryId;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FlightSchoolDemo:ServiceAccount:ClientId"] = clientId,
                ["FlightSchoolDemo:ServiceAccount:ClientSecret"] = secret,
                ["FlightSchoolDemo:ServiceAccount:Name"] = "flight-school-setup",
            }).Build();

            await FlightSchoolDemoSetup.AddIfConfiguredAsync(scope.ServiceProvider, db, configuration, tenantId, adminId, DateTimeOffset.UtcNow);

            // A second repository, granted to nobody, standing for every other part of the tenant.
            var other = await db.Documents.SingleAsync(d => d.Id == DemoId.For(tenantId, FlightSchoolDemoSetup.RepositorySlug));
            var sibling = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ParentId = null,
                Name = "Finance",
                MaskVersionId = other.MaskVersionId,
                CreatedByUserId = adminId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Documents.Add(sibling);
            await db.SaveChangesAsync();
            otherRepositoryId = sibling.Id;
        }

        using var api = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));

        // It SEES the school and only the school.
        var repositories = await TestJson.Get(api, "/api/repositories");
        var visible = repositories.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(DemoId.For(tenantId, FlightSchoolDemoSetup.RepositorySlug), visible);
        Assert.DoesNotContain(otherRepositoryId, visible);

        // It reaches the Administration folder, which breaks inheritance, through its own explicit grant.
        var administrationId = DemoId.For(tenantId, $"folder/{FlightSchoolDemoSetup.AdministrationSlug}");
        using (var administration = await api.GetAsync($"/api/documents/{administrationId}"))
        {
            Assert.Equal(HttpStatusCode.OK, administration.StatusCode);
        }

        // It creates a pilot USER, which the demo needs and the Crypto account cannot do.
        using (var created = await api.PostAsJsonAsync("/api/users", new
        {
            email = $"pilot-{Guid.NewGuid():N}@e2e.local",
            displayName = "Pilot",
            password = "Passw0rd!Passw0rd!",
        }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // And it is NOT an administrator: no tenant settings, no service-account management.
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
        var tenantId = await factory.SeedTenantNamedAsync($"fs-none-{Guid.NewGuid():N}");
        var adminId = await factory.SeedUserAsync(tenantId, $"admin-{Guid.NewGuid():N}@e2e.local", "Passw0rd!Passw0rd!", "Admin", isTenantAdmin: true);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        await FlightSchoolDemoSetup.AddIfConfiguredAsync(scope.ServiceProvider, db,
            new ConfigurationBuilder().Build(), tenantId, adminId, DateTimeOffset.UtcNow);

        Assert.False(await db.Documents.AnyAsync(d => d.Id == DemoId.For(tenantId, FlightSchoolDemoSetup.RepositorySlug)));
        Assert.False(await db.ServiceAccounts.AnyAsync());
    }
}
