using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising version restore / rollback (ADR "Version restore"): an
// older version's content is reinstated as a new current version (non-destructive), copying its document date;
// a non-editor is refused; and a restore is blocked while a workflow is in progress.
[Collection(E2ECollection.Name)]
public class VersionRestoreTests
{
    private readonly E2EApiFactory _factory;

    public VersionRestoreTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Restore_reinstates_an_old_version_and_is_blocked_for_non_editors_and_mid_workflow()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "restore-1234";
        var adminEmail = $"restore-admin-{Guid.NewGuid():N}@e2e.local";
        var adminId = await _factory.SeedUserAsync(tenantId, adminEmail, password, "Restore Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));

        var outsiderEmail = $"restore-out-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, outsiderEmail, password, "Outsider");
        using var outsider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(outsiderEmail, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Restore {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "restore-doc" })).GetProperty("id").GetGuid();

        var contentA = $"version-A-{Guid.NewGuid():N}";
        var v1Id = await AddVersionAsync(owner, docId, contentA, documentDate: "2020-01-15");
        await AddVersionAsync(owner, docId, $"version-B-{Guid.NewGuid():N}");

        // A non-editor can't restore.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync($"/api/documents/{docId}/versions/{v1Id}/restore", null)).StatusCode);

        // The admin restores v1 → a new confirmed version whose content is v1's and whose document date matches.
        var restored = await TestJson.Post(admin, $"/api/documents/{docId}/versions/{v1Id}/restore", new { });
        var restoredId = restored.GetProperty("id").GetGuid();
        Assert.Equal("Confirmed", restored.GetProperty("status").GetString());
        Assert.Equal("2020-01-15", restored.GetProperty("documentDate").GetString());
        Assert.NotEqual(v1Id, restoredId);

        // Its downloaded bytes equal version A.
        var download = restored.GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString();
        using (var storage = new HttpClient())
        {
            Assert.Equal(contentA, await (await storage.GetAsync(download)).Content.ReadAsStringAsync());
        }

        // Put the document under review, then a restore is refused 409 WORKFLOW_IN_PROGRESS.
        (await admin.PostAsJsonAsync($"/api/documents/{docId}/versions/{restoredId}/workflow/submit", new { reviewerId = adminId })).EnsureSuccessStatusCode();
        var blocked = await admin.PostAsync($"/api/documents/{docId}/versions/{v1Id}/restore", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("WORKFLOW_IN_PROGRESS", await blocked.Content.ReadAsStringAsync());
    }

    // Creates + finalizes a confirmed version with the given text content; returns its id.
    private async Task<Guid> AddVersionAsync(HttpClient client, Guid docId, string content, string? documentDate = null)
    {
        var body = documentDate is null ? (object)new { fileExtension = ".txt" } : new { fileExtension = ".txt", documentDate };
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", body);
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }
        var versionId = created.GetProperty("id").GetGuid();
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
        return versionId;
    }
}
