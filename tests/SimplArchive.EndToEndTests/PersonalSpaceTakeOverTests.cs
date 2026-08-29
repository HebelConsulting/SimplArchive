using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// Taking over a personal space (ADR 0672, #702 PR 3).
//
// Privacy means no SILENT access, not no access — offboarding and GDPR need a way into a space nobody else can
// reach. So the way in is explicit, audited, announced to the owner, and leaves an ordinary ACL entry that is
// revoked like any other. Each of those four is asserted, because any one of them missing turns a deliberate
// exception into exactly the quiet back door the privacy rule exists to remove.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class PersonalSpaceTakeOverTests
{
    private readonly E2EApiFactory _factory;

    public PersonalSpaceTakeOverTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_admin_takes_over_a_space_it_is_audited_announced_and_revocable()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var adminEmail = $"takeover-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "adm-1234", "Admin", canManageUsers: true, canViewAuditLog: true);
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "adm-1234"));

        var ownerEmail = $"owner-{Guid.NewGuid():N}@e2e.local";
        var ownerId = await _factory.SeedUserAsync(tenantId, ownerEmail, "u-1234", "Owner");
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(ownerEmail, "u-1234"));
        var ownerRepo = (await TestJson.Post(owner, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();

        // The affordance is FOLLOWED, not composed: the listing row advertises it, which is also the assertion
        // that a client can reach this endpoint at all (ADR 0543).
        var row = (await TestJson.Get(admin, "/api/admin/personal-repositories"))
            .GetProperty("repositories").EnumerateArray()
            .Single(r => r.GetProperty("userId").GetGuid() == ownerId);
        var takeOverHref = row.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "take-over")
            .GetProperty("href").GetString()!;

        var result = await TestJson.Post(admin, takeOverHref, new { });
        Assert.Equal(ownerRepo, result.GetProperty("repositoryId").GetGuid());

        // 1. The access is real — the admin can now read the space they had no grant on.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{ownerRepo}/children")).StatusCode);

        // 2. It is recorded.
        var audit = await TestJson.Get(admin, "/api/audit-events?action=PersonalSpace.TakenOver");
        Assert.Contains(audit.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("action").GetString() == "PersonalSpace.TakenOver");

        // 3. The owner is TOLD, which is the property that makes this not a back door. Read as the owner: a
        //    notification the owner cannot see would be an audit entry wearing a notification's name.
        var notifications = await TestJson.Get(owner, "/api/notifications");
        Assert.Contains(notifications.GetProperty("notifications").EnumerateArray(),
            n => n.GetProperty("title").GetString()!.Contains("took over", StringComparison.OrdinalIgnoreCase));

        // 4. It is an ORDINARY grant, so it appears where people look for who-can-see-what and is revoked the
        //    same way. Deliberately no bespoke "release" action: the exception is visible rather than hidden.
        //
        //    Keyed on the ADMIN's own id, not merely "some full-rights entry": the OWNER already has one, so
        //    the loose version of this assertion would have passed without any take-over happening at all.
        var adminId = (await TestJson.Get(admin, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid();
        var entries = await TestJson.Get(admin, $"/api/documents/{ownerRepo}/acl-entries");
        var granted = entries.GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("principalId").GetGuid() == adminId);

        Assert.True(granted.GetProperty("canManagePermissions").GetBoolean());

        // Revoked the ordinary way — proven by DOING it, not by naming a rel. This asserted the presence of a
        // `remove` rel until ADR 0719 collapsed the entry's three same-address rels into `self` (the method is
        // the action); following the address and checking the grant is gone survives that rename and states the
        // property the test is actually about, which naming a rel never did.
        var address = granted.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "self").GetProperty("href").GetString()!;
        (await admin.DeleteAsync(address)).EnsureSuccessStatusCode();

        // Read back as the OWNER, not the admin: revoking that grant is what the admin's own access to this
        // space rested on, so asking them to observe the result is asking them to look through the door they
        // just closed. The owner is the honest witness — and the one who cares.
        var after = await TestJson.Get(owner, $"/api/documents/{ownerRepo}/acl-entries");
        Assert.DoesNotContain(
            after.GetProperty("entries").EnumerateArray(), e => e.GetProperty("principalId").GetGuid() == adminId);
    }

    [Fact]
    public async Task Asking_twice_is_not_an_error()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var adminEmail = $"twice-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "adm-1234", "Admin", canManageUsers: true);
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "adm-1234"));

        var ownerEmail = $"twice-owner-{Guid.NewGuid():N}@e2e.local";
        var ownerId = await _factory.SeedUserAsync(tenantId, ownerEmail, "u-1234", "Owner");
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(ownerEmail, "u-1234"));
        await TestJson.Post(owner, "/api/me/personal-repository", new { });

        var href = $"/api/admin/personal-repositories/{ownerId}/take-over";

        // AclEntry carries a partial unique index per principal per document, so a naive insert would make the
        // second call a 500. Asking for access you already hold is not an error either.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(href, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(href, new { })).StatusCode);
    }

    [Fact]
    public async Task Without_CanManageUsers_there_is_neither_a_rel_nor_an_endpoint()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        // A tenant admin who may browse personal spaces but is NOT a user manager. The two rights are separate
        // on purpose: reading somebody's space and taking it over are different acts.
        var browserEmail = $"browser-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, browserEmail, "adm-1234", "Browser");
        await _factory.GrantTenantAdminAsync(browserEmail);
        using var browser = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(browserEmail, "adm-1234"));

        var ownerEmail = $"nogate-owner-{Guid.NewGuid():N}@e2e.local";
        var ownerId = await _factory.SeedUserAsync(tenantId, ownerEmail, "u-1234", "Owner");
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(ownerEmail, "u-1234"));
        await TestJson.Post(owner, "/api/me/personal-repository", new { });

        var row = (await TestJson.Get(browser, "/api/admin/personal-repositories"))
            .GetProperty("repositories").EnumerateArray()
            .Single(r => r.GetProperty("userId").GetGuid() == ownerId);

        // The rel is absent — a client disables the affordance rather than offering a button that 403s.
        Assert.DoesNotContain(row.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "take-over");

        // ...and the endpoint refuses anyway, because a missing rel is guidance, not enforcement.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await browser.PostAsJsonAsync($"/api/admin/personal-repositories/{ownerId}/take-over", new { })).StatusCode);
    }
}
