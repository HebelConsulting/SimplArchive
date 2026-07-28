using System.Net;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage, exercising the tenant-admin Administration → Users
// view (ADR "Tenant-admin Administration → Users view"): a tenant admin lists every user's personal repository and
// can browse into a personal space (via the IsTenantAdmin ACL bypass); the listing is recorded to the audit log;
// a non-admin is refused.
[Collection(E2ECollection.Name)]
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
        var aliceDoc = await UploadAsync(alice, aliceRepo, "alice-secret");

        // The admin lists every personal repository — both users' spaces are present.
        var list = await TestJson.Get(admin, "/api/admin/personal-repositories");
        var repos = list.GetProperty("repositories").EnumerateArray().ToList();
        Assert.Contains(repos, r => r.GetProperty("repositoryId").GetGuid() == aliceRepo && r.GetProperty("displayName").GetString() == "Alice");
        Assert.Contains(repos, r => r.GetProperty("repositoryId").GetGuid() == bobRepo);

        // The admin can browse into Alice's personal space (IsTenantAdmin ACL bypass) and see her private document.
        var children = await TestJson.Get(admin, $"/api/documents/{aliceRepo}/children");
        Assert.Contains(children.GetProperty("children").EnumerateArray(), c => c.GetProperty("id").GetGuid() == aliceDoc);

        // The access was recorded to the audit log.
        var audit = await TestJson.Get(admin, "/api/audit-events?action=Admin.ViewedPersonalSpaces");
        Assert.Contains(audit.GetProperty("events").EnumerateArray(), e => e.GetProperty("action").GetString() == "Admin.ViewedPersonalSpaces");

        // A non-admin (Alice) cannot list personal repositories.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/admin/personal-repositories")).StatusCode);
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
}
