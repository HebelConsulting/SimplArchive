using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + OpenSearch, exercising document tags (ADR "Document tags"):
// setting tags normalizes/dedupes them, the tenant tag catalog lists them, a non-editor is refused, and a
// tagged document is found via system[tag] search with a Tags facet. Async indexing, so polled to consistency.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentTagsTests
{
    private readonly E2EApiFactory _factory;

    public DocumentTagsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Tags_normalize_list_gate_and_drive_search()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A user with no rights on the document (can't edit index data).
        var email = $"tags-out-{Guid.NewGuid():N}@e2e.local";
        const string password = "tags-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Outsider");
        using var outsider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Tags-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var word = $"tagword{Guid.NewGuid():N}"[..14];
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "tag-doc" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(word)))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        // Set tags: mixed case + a duplicate + blank → normalized (trimmed lowercase), deduped, sorted.
        var set = await TestJson.Put(owner, $"/api/documents/{docId}/tags", new { tags = new[] { "Urgent", "  Q3-2026 ", "urgent", "" } });
        var tags = set.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(new[] { "q3-2026", "urgent" }, tags);

        // The tenant tag catalog lists them.
        var catalog = (await TestJson.Get(owner, "/api/tags")).GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("urgent", catalog);
        Assert.Contains("q3-2026", catalog);

        // A non-editor can't set tags.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutAsJsonAsync($"/api/documents/{docId}/tags", new { tags = new[] { "nope" } })).StatusCode);

        // Search: once indexed, system[tag][eq]=urgent finds the document and the broad search carries a Tags facet.
        await PollAsync(async () => (await SearchIdsAsync(owner, word, "&system[tag][eq]=urgent")).Contains(docId), "the tag is indexed");
        var facets = (await TestJson.Get(owner, $"/api/search?q={word}")).GetProperty("facets");
        Assert.Contains(facets.GetProperty("tags").EnumerateArray(), b => b.GetProperty("value").GetString() == "urgent");
    }

    private static async Task<HashSet<Guid>> SearchIdsAsync(HttpClient client, string q, string extra = "")
    {
        var response = await TestJson.Get(client, $"/api/search?q={Uri.EscapeDataString(q)}{extra}");
        return response.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
    }

    private static async Task PollAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {timeoutSeconds}s waiting for: {what}");
    }
}
