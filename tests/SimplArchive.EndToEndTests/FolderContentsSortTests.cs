using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the per-folder contents sort order (ADR "Per-folder
// contents sort order"): the children envelope carries the folder's sort order (defaulting to DocumentDate) +
// each child's versionCreatedAt, PUT sets it, an undefined value is rejected, and a non-editor is refused.
[Collection(E2ECollection.Name)]
public class FolderContentsSortTests
{
    private readonly E2EApiFactory _factory;

    public FolderContentsSortTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Folder_sort_order_defaults_sets_projects_and_gates()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A user with no rights on the folder (can't edit its index data).
        var email = $"fsort-out-{Guid.NewGuid():N}@e2e.local";
        const string password = "fsort-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Outsider");
        using var outsider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"FSort-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "sortme" })).GetProperty("id").GetGuid();

        // A child document with a confirmed version, so versionCreatedAt is projected.
        var docId = (await TestJson.Post(owner, $"/api/documents/{folderId}/children", new { name = "child" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        // The children envelope defaults to DocumentDate (1) and projects each child's versionCreatedAt.
        var listing = await owner.GetFromJsonAsync<JsonElement>($"/api/documents/{folderId}/children");
        Assert.Equal(1, listing.GetProperty("contentsSortOrder").GetInt32());
        var child = listing.GetProperty("children").EnumerateArray().First();
        Assert.NotEqual(JsonValueKind.Null, child.GetProperty("versionCreatedAt").ValueKind);

        // Set it to Name (0) and read it back off the envelope.
        Assert.Equal(0, (await TestJson.Put(owner, $"/api/documents/{folderId}/contents-sort-order", new { sortOrder = 0 })).GetProperty("contentsSortOrder").GetInt32());
        Assert.Equal(0, (await owner.GetFromJsonAsync<JsonElement>($"/api/documents/{folderId}/children")).GetProperty("contentsSortOrder").GetInt32());

        // An undefined value → 400; a non-editor → 403.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/documents/{folderId}/contents-sort-order", new { sortOrder = 99 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutAsJsonAsync($"/api/documents/{folderId}/contents-sort-order", new { sortOrder = 2 })).StatusCode);
    }
}
