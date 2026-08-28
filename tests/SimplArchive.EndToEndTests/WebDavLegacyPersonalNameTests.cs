using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// A personal space provisioned before ADR 0671 is still named "Personal" for the life of the deployment —
// invisible on the nightly-reset kiosk, permanent on an upgraded installation (#795). Two halves, asserted
// together because either alone re-opens the hole: the startup HEAL renames the space to its owner (one DB
// row, so every surface — web tree, desktop, IMAP, DAV — becomes consistent at once), and the WebDAV ALIAS
// keeps a mount saved against the old /Personal segment serving (the /webdav → /SimplArchive recipe: the
// canonical name moves, the alias serves).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class WebDavLegacyPersonalNameTests
{
    private readonly E2EApiFactory _factory;

    public WebDavLegacyPersonalNameTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_legacy_Personal_space_is_healed_and_its_old_mount_path_still_serves()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"legacy-{Guid.NewGuid():N}@e2e.local";
        const string password = "legacy-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Legacy Owner");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        // First WebDAV touch provisions the space under its owner's name; regress it to the pre-ADR-0671
        // state the way an upgraded install actually is: the row simply says "Personal".
        async Task<HttpStatusCode> PropfindAsync(string path)
        {
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), path) { Headers = { Authorization = basic } };
            req.Headers.Add("Depth", "0");
            return (await dav.SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive/Legacy Owner"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var space = await db.Documents.IgnoreQueryFilters(["TenantFilter"])
                .SingleAsync(d => d.TenantId == tenantId && d.PersonalOfUserId != null
                    && db.Users.Any(u => u.Id == d.PersonalOfUserId && u.Email == email));
            space.Name = PersonalRepositoryProvisioner.LegacyPersonalRepositoryName;
            await db.SaveChangesAsync();
        }

        // The legacy state is real: the owner-named path is gone, the old constant answers.
        Assert.Equal(HttpStatusCode.NotFound, await PropfindAsync("/SimplArchive/Legacy Owner"));
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive/Personal"));

        // The startup heal (run here exactly as Program.cs runs it) renames the row to the owner…
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            await LegacyPersonalSpaceHealer.HealAsync(db, NullLogger.Instance);
            var healed = await db.Documents.IgnoreQueryFilters(["TenantFilter"])
                .SingleAsync(d => d.TenantId == tenantId && d.PersonalOfUserId != null
                    && db.Users.Any(u => u.Id == d.PersonalOfUserId && u.Email == email));
            Assert.Equal("Legacy Owner", healed.Name);
        }

        // …and BOTH paths serve: the canonical name, and the alias the saved mount still uses.
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive/Legacy Owner"));
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive/Personal"));

        // The alias reaches INTO the space too — a mount addresses folders, not just the root.
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive/Personal/My Documents"));
    }

    [Fact]
    public async Task A_shared_repository_with_the_owners_name_does_not_block_the_heal()
    {
        // Personal spaces live in a per-user namespace (the partial unique index) and are exempt from the
        // tenant-wide root sibling rule BY DESIGN — so a repository named like the owner is legal beside the
        // healed space, the heal proceeds, and WebDAV still resolves the OWNER's segment to their personal
        // space (the resolver prefers it). First written expecting the rename to be refused; the invariant's
        // own exemption said otherwise, which is exactly why this pin exists.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"clash-{Guid.NewGuid():N}@e2e.local";
        const string password = "clash-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Clash Owner");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        using var saClient = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        await TestJson.Post(saClient, "/api/repositories", new { name = "Clash Owner" });

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();
        async Task<HttpStatusCode> PropfindAsync(string path)
        {
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), path) { Headers = { Authorization = basic } };
            req.Headers.Add("Depth", "0");
            return (await dav.SendAsync(req)).StatusCode;
        }

        // Any authenticated DAV request provisions the space; the name it takes does not matter, because the
        // next block regresses it to the legacy constant either way.
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var space = await db.Documents.IgnoreQueryFilters(["TenantFilter"])
                .SingleAsync(d => d.TenantId == tenantId && d.PersonalOfUserId != null
                    && db.Users.Any(u => u.Id == d.PersonalOfUserId && u.Email == email));
            space.Name = PersonalRepositoryProvisioner.LegacyPersonalRepositoryName;
            await db.SaveChangesAsync();

            await LegacyPersonalSpaceHealer.HealAsync(db, NullLogger.Instance);

            var after = await db.Documents.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(d => d.Id == space.Id);
            Assert.Equal("Clash Owner", after.Name);
        }

        // The owner's segment resolves to THEIR personal space (not the same-named repository), and the
        // legacy alias serves too — a saved mount survives the heal whatever the tenant's repositories are called.
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive/Clash Owner"));
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive/Clash Owner/My Documents"));
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync("/SimplArchive/Personal/My Documents"));
    }
}
