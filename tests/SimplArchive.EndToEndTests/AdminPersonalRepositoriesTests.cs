using System.Net;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage, exercising the Administration → Users view
// (ADR "Tenant-admin Administration → Users view"): an administrator lists every user's personal repository and
// can browse into a personal space; the listing is recorded to the audit log; a non-admin is refused.
//
// What grants that access changed with ADR 0670. It is NO LONGER the IsTenantAdmin bypass — which now stops at
// the edge of somebody else's personal space — but CanAccessWithoutGrant, which promotion grants and an
// administrator can hand back. The second test is the one that says so: same admin, same tenant, right revoked,
// and the whole surface goes quiet rather than half-working.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class AdminPersonalRepositoriesTests
{
    private readonly E2EApiFactory _factory;

    public AdminPersonalRepositoriesTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_lists_and_browses_personal_spaces_it_is_audited_and_non_admins_are_refused()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var adminEmail = $"admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "adm-1234", "Admin", canViewAuditLog: true);
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "adm-1234"));

        // Two ordinary users, each with a personal repo holding a private document.
        var aliceEmail = $"alice-{Guid.NewGuid():N}@e2e.local";
        var bobEmail = $"bob-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, aliceEmail, "u-1234", "Alice");
        await _factory.SeedUserAsync(tenantId, bobEmail, "u-1234", "Bob");
        using var alice = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(aliceEmail, "u-1234"));
        using var bob = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(bobEmail, "u-1234"));

        var aliceRepo = (await TestJson.Post(alice, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var bobRepo = (await TestJson.Post(bob, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var aliceDoc = await UploadAsync(alice, await MyDocumentsAsync(alice, aliceRepo), "alice-secret");

        // The admin lists every personal repository — both users' spaces are present.
        var list = await TestJson.Get(admin, "/api/admin/personal-repositories");
        var repos = list.GetProperty("repositories").EnumerateArray().ToList();
        Assert.Contains(repos, r => r.GetProperty("repositoryId").GetGuid() == aliceRepo && r.GetProperty("displayName").GetString() == "Alice");
        Assert.Contains(repos, r => r.GetProperty("repositoryId").GetGuid() == bobRepo);

        // The admin can browse into Alice's personal space (CanAccessWithoutGrant) and see her private
        // document — which sits in My Documents, since the first level holds only provisioned folders (#634).
        // The bypass is what this asserts, so it browses with the ADMIN's client all the way down.
        var children = await TestJson.Get(admin, $"/api/documents/{await MyDocumentsAsync(admin, aliceRepo)}/children");
        Assert.Contains(children.GetProperty("children").EnumerateArray(), c => c.GetProperty("id").GetGuid() == aliceDoc);

        // The access was recorded to the audit log.
        var audit = await TestJson.Get(admin, "/api/audit-events?action=Admin.ViewedPersonalSpaces");
        Assert.Contains(audit.GetProperty("events").EnumerateArray(), e => e.GetProperty("action").GetString() == "Admin.ViewedPersonalSpaces");

        // A non-admin (Alice) cannot list personal repositories.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/admin/personal-repositories")).StatusCode);
    }

    [Fact]
    public async Task An_admin_who_revoked_their_own_access_loses_the_rel_the_listing_and_the_browse()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var adminEmail = $"admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "adm-1234", "Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "adm-1234"));

        var carolEmail = $"carol-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, carolEmail, "u-1234", "Carol");
        using var carol = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(carolEmail, "u-1234"));
        var carolRepo = (await TestJson.Post(carol, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var carolFolder = await MyDocumentsAsync(carol, carolRepo);
        var carolDoc = await UploadAsync(carol, carolFolder, "carol-secret");

        // While the admin holds the right, everything works — the control, so the assertions below cannot pass
        // for the trivial reason that the fixture never granted anything.
        Assert.Contains(
            (await TestJson.Get(admin, "/api/admin/personal-repositories")).GetProperty("repositories").EnumerateArray(),
            r => r.GetProperty("repositoryId").GetGuid() == carolRepo);

        await _factory.RevokeAccessWithoutGrantAsync(adminEmail);

        // The rel goes first: a client is supposed to be able to follow what it is offered (ADR 0543), so an
        // affordance that would answer 403 must not be advertised at all.
        Assert.DoesNotContain(
            (await TestJson.Get(admin, "/api/admin")).GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "personal-repositories");

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/personal-repositories")).StatusCode);

        // ...and the space itself is genuinely closed, not merely unlisted. Still a tenant admin: this is the
        // narrowed bypass, so the SAME caller keeps full rights outside the personal space.
        //
        // 403 rather than 404 is the API's existing answer for "no CanSee" everywhere, not something this
        // change chose — worth knowing, because it means the refusal admits the id exists. Enumerating ids is
        // what the listing above no longer allows, so this is a much smaller door than the one just closed.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/documents/{carolDoc}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/repositories")).StatusCode);
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid folderId, string name)
    {
        var docId = (await TestJson.Post(client, $"/api/documents/{folderId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes(name)))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
        return docId;
    }

    // The personal space's first level holds only the folders it was provisioned with (#634), so a test that
    // wants somewhere to put things asks for My Documents — which is where a user's own content goes.
    private static async Task<Guid> MyDocumentsAsync(HttpClient api, Guid personalId) =>
        (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Documents")
            .GetProperty("id").GetGuid();


    [Fact]
    public async Task A_listed_personal_space_advertises_everything_browsing_it_needs()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var adminEmail = $"admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "adm-1234", "Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "adm-1234"));

        var userEmail = $"carol-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, userEmail, "u-1234", "Carol");
        using var carol = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(userEmail, "u-1234"));
        var repo = (await TestJson.Post(carol, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();

        var row = (await TestJson.Get(admin, "/api/admin/personal-repositories"))
            .GetProperty("repositories").EnumerateArray()
            .Single(r => r.GetProperty("repositoryId").GetGuid() == repo);
        var rels = row.GetProperty("links").EnumerateArray()
            .ToDictionary(l => l.GetProperty("rel").GetString()!, l => l.GetProperty("href").GetString()!);

        // Opening a folder lists its children AND the shortcuts filed in it, so a row a client opens FROM must
        // carry both (#735). It carried only `children`, and the desktop tree — which follows both — died on
        // the one that was never advertised, on a path with no handler above it.
        //
        // Asserted here rather than in the client, because the client now degrades when a rel is missing: it
        // no longer crashes, so nothing over there can notice this rel disappearing again, and the symptom
        // would be shortcuts silently absent from every admin-browsed space.
        Assert.Contains("children", rels.Keys);
        Assert.Contains("references", rels.Keys);
        Assert.Contains("document", rels.Keys);

        // Followed, not merely present: a rel that 404s is worse than an absent one.
        foreach (var rel in new[] { "children", "references", "document" })
        {
            Assert.True(
                (await admin.GetAsync(rels[rel])).IsSuccessStatusCode,
                $"the '{rel}' rel the admin listing advertised did not answer");
        }
    }
}
