using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising version restore via the CurrentVersionId pointer (ADR
// "Version-restore via a current-version pointer", issue #265): making an older version current pins THAT existing
// version (no copy) — so its annotations / document date are preserved and the version count is unchanged; the
// versions list reports it as current; uploading a new version clears the pointer (the new one becomes current);
// a non-editor is refused; and a restore is blocked while a workflow is in progress.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class VersionRestoreTests
{
    private readonly E2EApiFactory _factory;

    public VersionRestoreTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Make_current_pins_an_existing_version_without_copying_and_a_new_upload_takes_over()
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

        // Pin a sticky-note annotation to v1 — the whole point of the pointer approach is that making v1 current
        // preserves it (the old copy-based restore created a fresh version with no annotations).
        var v1Annotations = $"/api/documents/{docId}/versions/{v1Id}/annotations";
        await TestJson.Post(owner, v1Annotations, new { pageIndex = 0, positionX = 0.25, positionY = 0.4, text = "note on v1", color = "#FFEB3B" });

        // A non-editor can't make a version current.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync($"/api/documents/{docId}/versions/{v1Id}/restore", null)).StatusCode);

        // The admin makes v1 current → the SAME v1 (no new version), its document date intact.
        var restored = await TestJson.Post(admin, $"/api/documents/{docId}/versions/{v1Id}/restore", new { });
        Assert.Equal(v1Id, restored.GetProperty("id").GetGuid());          // pinned the existing version, not a copy
        Assert.Equal("Confirmed", restored.GetProperty("status").GetString());
        Assert.Equal("2020-01-15", restored.GetProperty("documentDate").GetString());

        // The versions list still holds exactly the two versions, and reports v1 as current.
        var list = await TestJson.Get(admin, $"/api/documents/{docId}/versions");
        Assert.Equal(2, list.GetProperty("versions").EnumerateArray().Count(v => v.GetProperty("status").GetString() == "Confirmed"));
        Assert.Equal(v1Id, list.GetProperty("currentVersionId").GetGuid());

        // v1's annotation survived (it's the same version, never copied).
        var annos = await TestJson.Get(admin, v1Annotations);
        Assert.Equal(1, annos.GetProperty("annotations").GetArrayLength());

        // The current version's download bytes equal version A.
        var v1Resource = await TestJson.Get(admin, $"/api/documents/{docId}/versions/{v1Id}");
        var download = v1Resource.GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString();
        using (var storage = new HttpClient())
        {
            Assert.Equal(contentA, await (await storage.GetAsync(download)).Content.ReadAsStringAsync());
        }

        // Uploading a new version clears the pointer → the new one (v3) becomes current.
        var v3Id = await AddVersionAsync(owner, docId, $"version-C-{Guid.NewGuid():N}");
        var list2 = await TestJson.Get(admin, $"/api/documents/{docId}/versions");
        Assert.Equal(v3Id, list2.GetProperty("currentVersionId").GetGuid());

        // Put the document under review, then making a version current is refused 409 WORKFLOW_IN_PROGRESS.
        (await admin.PostAsJsonAsync($"/api/documents/{docId}/versions/{v3Id}/workflow/submit", new { reviewerId = adminId })).EnsureSuccessStatusCode();
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
