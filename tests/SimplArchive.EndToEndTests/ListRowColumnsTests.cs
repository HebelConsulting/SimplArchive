using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the list-row columns (ADR "List-row columns and
// sorting"): a child listing carries the document type (assigned mask name), the latest version's document
// date + byte size, and the tags — all derived/projected, no schema change.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ListRowColumnsTests
{
    private readonly E2EApiFactory _factory;

    public ListRowColumnsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Child_listing_carries_type_date_size_and_tags()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Cols {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "col-doc" })).GetProperty("id").GetGuid();

        // A confirmed version → auto-classifies to "Basic Entry" (documentType) + stamps size + document date.
        var bytes = Encoding.UTF8.GetBytes(new string('x', 4096));
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        await TestJson.Put(owner, $"/api/documents/{docId}/tags", new { tags = new[] { "Blue", "green" } });

        var row = (await TestJson.Get(owner, $"/api/documents/{repoId}/children")).GetProperty("children")
            .EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == docId);

        Assert.Equal("Basic Entry", row.GetProperty("documentType").GetString());
        Assert.Equal(4096, row.GetProperty("sizeBytes").GetInt64());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("documentDate").GetString())); // a "yyyy-MM-dd" date
        Assert.Equal(new[] { "blue", "green" }, row.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }
}
