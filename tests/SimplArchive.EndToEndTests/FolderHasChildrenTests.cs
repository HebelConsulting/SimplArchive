using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// `hasChildren` means "is anything filed here at all" (issue #376). It used to count only child Documents, so a
// folder holding nothing but REFERENCES (shortcuts) reported false — and the tree drew it with the empty-folder
// glyph even though the contents list would show its shortcuts.
//
// The flag is what both clients' empty-folder icon keys on, and it also backs their "is there something to open"
// tests, so this pins the corrected meaning across the endpoints that compute it independently.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class FolderHasChildrenTests
{
    private readonly E2EApiFactory _factory;

    public FolderHasChildrenTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_folder_holding_only_a_reference_is_not_reported_as_empty()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Refs {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // A document to point at, and an otherwise-empty folder to file the shortcut into.
        var targetId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "target-doc" })).GetProperty("id").GetGuid();
        var shortcutsId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "shortcuts" })).GetProperty("id").GetGuid();

        // Genuinely empty to begin with.
        Assert.False(await HasChildrenAsync(api, repoId, shortcutsId));

        // File a reference into it. It now holds something, even though it still has no child Document.
        await PostJson(api, $"/api/documents/{shortcutsId}/references", new { targetId });

        Assert.True(await HasChildrenAsync(api, repoId, shortcutsId),
            "a folder holding a reference must not report hasChildren=false — the tree would draw it as empty");

        // And the contents list does show the shortcut, which is why reporting it empty was wrong.
        var contents = (await GetJson(api, $"/api/documents/{shortcutsId}/children")).GetProperty("children").EnumerateArray().ToList();
        var references = (await GetJson(api, $"/api/documents/{shortcutsId}/references")).GetProperty("references").EnumerateArray().ToList();
        Assert.Empty(contents);
        Assert.Single(references);
    }

    // A folder with a child document was always reported correctly; this guards the widening from having broken
    // the ordinary case.
    [Fact]
    public async Task A_folder_holding_a_document_is_still_reported_as_having_children()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Docs {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "with-doc" })).GetProperty("id").GetGuid();

        Assert.False(await HasChildrenAsync(api, repoId, folderId));

        await PostJson(api, $"/api/documents/{folderId}/children", new { name = "inner-doc" });

        Assert.True(await HasChildrenAsync(api, repoId, folderId));
    }

    private static async Task<bool> HasChildrenAsync(HttpClient api, Guid parentId, Guid folderId) =>
        (await GetJson(api, $"/api/documents/{parentId}/children")).GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == folderId)
            .GetProperty("hasChildren").GetBoolean();

    private static async Task<JsonElement> PostJson(HttpClient api, string url, object body)
    {
        var response = await api.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> GetJson(HttpClient api, string url)
    {
        var response = await api.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }
}
