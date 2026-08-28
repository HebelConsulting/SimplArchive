using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the tag catalog (ADR "Tag controlled vocabulary"): a
// tenant admin creates/renames/merges/retires catalog tags (rename + merge cascade the document tag strings),
// a non-admin is refused, and the per-tenant enforcement toggle rejects a non-catalog tag.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class TagCatalogTests
{
    private readonly E2EApiFactory _factory;

    public TagCatalogTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Catalog_create_rename_merge_retire_and_enforce()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"tagadmin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "tag-1234", "Tag Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "tag-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Cat-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "cat-doc" })).GetProperty("id").GetGuid();

        // Free-form tagging auto-populates the catalog.
        await TestJson.Put(owner, $"/api/documents/{docId}/tags", new { tags = new[] { "alpha", "beta" } });

        var catalog = await TestJson.Get(admin, "/api/tags");
        Assert.True(catalog.GetProperty("canManage").GetBoolean());
        var alphaId = catalog.GetProperty("catalog").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "alpha").GetProperty("id").GetGuid();

        // Create a coloured catalog tag; a non-admin can't manage.
        var gamma = await TestJson.Post(admin, "/api/tags", new { name = "Gamma", color = "#FF0000" });
        Assert.Equal("gamma", gamma.GetProperty("name").GetString());
        Assert.Equal("#FF0000", gamma.GetProperty("color").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/tags", new { name = "nope" })).StatusCode);

        // Rename alpha → alpha2: the document's tag string updates.
        await TestJson.Put(admin, $"/api/tags/{alphaId}", new { name = "alpha2" });
        Assert.Contains("alpha2", await DocTagsAsync(owner, docId));
        Assert.DoesNotContain("alpha", await DocTagsAsync(owner, docId));

        // Merge beta → alpha2: the doc keeps alpha2 (deduped), beta is gone from the catalog.
        var betaId = await TagIdAsync(admin, "beta");
        var alpha2Id = await TagIdAsync(admin, "alpha2");
        (await admin.PostAsJsonAsync($"/api/tags/{betaId}/merge", new { intoId = alpha2Id })).EnsureSuccessStatusCode();
        Assert.Contains("alpha2", await DocTagsAsync(owner, docId));
        Assert.DoesNotContain("beta", await DocTagsAsync(owner, docId));
        Assert.DoesNotContain("beta", await CatalogNamesAsync(admin));

        // Retire gamma → excluded from the catalog.
        (await admin.DeleteAsync($"/api/tags/{gamma.GetProperty("id").GetGuid()}")).EnsureSuccessStatusCode();
        Assert.DoesNotContain("gamma", await CatalogNamesAsync(admin));

        // Enforcement: restrict → a non-catalog tag PUT is refused; an in-catalog one still works.
        await _factory.SetTenantRestrictTagsToCatalogAsync(tenantId, true);
        var refused = await owner.PutAsJsonAsync($"/api/documents/{docId}/tags", new { tags = new[] { "not-in-catalog" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("UNKNOWN_TAG", await refused.Content.ReadAsStringAsync());
        Assert.True((await owner.PutAsJsonAsync($"/api/documents/{docId}/tags", new { tags = new[] { "alpha2" } })).IsSuccessStatusCode);
    }

    private static async Task<List<string?>> DocTagsAsync(HttpClient client, Guid docId) =>
        (await TestJson.Get(client, $"/api/documents/{docId}/tags")).GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();

    private static async Task<List<string?>> CatalogNamesAsync(HttpClient client) =>
        (await TestJson.Get(client, "/api/tags")).GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();

    private static async Task<Guid> TagIdAsync(HttpClient client, string name) =>
        (await TestJson.Get(client, "/api/tags")).GetProperty("catalog").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
}
