using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for data-classification / sensitivity labels (ADR "Data classification / sensitivity labels") over
// the real API + Postgres + OpenSearch: a label is set + reflected on the document, validated, gated on
// CanEditIndexData, and filterable in search (system[sensitivityLabel]).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class SensitivityLabelTests
{
    private readonly E2EApiFactory _factory;

    public SensitivityLabelTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Set_reflect_validate_authorize_and_search()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // The tenant's seeded default labels are present; pick Confidential (watermarked).
        var labels = (await TestJson.Get(owner, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray().ToList();
        var confidential = labels.Single(l => l.GetProperty("name").GetString() == "Confidential");
        var confidentialId = confidential.GetProperty("id").GetGuid();
        Assert.True(confidential.GetProperty("watermark").GetBoolean());

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Sens {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var word = $"sensword{Guid.NewGuid():N}";
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "labeled-doc" })).GetProperty("id").GetGuid();
        await UploadVersionAsync(owner, docId, $"content {word}\n");

        // Default is None (null).
        Assert.Equal(JsonValueKind.Null, (await TestJson.Get(owner, $"/api/documents/{docId}")).GetProperty("sensitivityLabelId").ValueKind);

        // Set to Confidential by id → reflected on the document (id + name + watermark).
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/sensitivity", new { labelId = confidentialId })).EnsureSuccessStatusCode();
        var doc = await TestJson.Get(owner, $"/api/documents/{docId}");
        Assert.Equal(confidentialId, doc.GetProperty("sensitivityLabelId").GetGuid());
        Assert.Equal("Confidential", doc.GetProperty("sensitivityLabelName").GetString());
        Assert.True(doc.GetProperty("sensitivityWatermark").GetBoolean());

        // An unknown label id → 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/documents/{docId}/sensitivity", new { labelId = Guid.NewGuid() })).StatusCode);

        // A caller without CanEditIndexData can't set it.
        var (otherClientId, otherSecret) = await _factory.SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: false);
        using var outsider = _factory.CreateAuthedClient(await _factory.GetTokenAsync(otherClientId, otherSecret));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutAsJsonAsync($"/api/documents/{docId}/sensitivity", new { labelId = confidentialId })).StatusCode);

        // Searchable: the sensitivity filter matches Confidential, not Public (once re-indexed).
        await PollAsync(async () =>
        {
            var hit = await SearchIdsAsync(owner, $"q={word}&system[sensitivityLabel][eq]=Confidential");
            return hit.Contains(docId);
        }, "doc indexed with the Confidential label");
        Assert.DoesNotContain(docId, await SearchIdsAsync(owner, $"q={word}&system[sensitivityLabel][eq]=Public"));
    }

    private static async Task UploadVersionAsync(HttpClient api, Guid docId, string content)
    {
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });
    }

    private static async Task<HashSet<Guid>> SearchIdsAsync(HttpClient api, string query)
    {
        var results = (await TestJson.Get(api, $"/api/search?{query}")).GetProperty("results");
        return results.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToHashSet();
    }

    [Fact]
    public async Task List_row_carries_the_label_and_search_facets_it()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var labels = (await TestJson.Get(owner, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray().ToList();
        var confidentialId = labels.Single(l => l.GetProperty("name").GetString() == "Confidential").GetProperty("id").GetGuid();
        var internalId = labels.Single(l => l.GetProperty("name").GetString() == "Internal").GetProperty("id").GetGuid();

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"SensFacet {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var word = $"sensfacet{Guid.NewGuid():N}";
        var confId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "conf" })).GetProperty("id").GetGuid();
        await UploadVersionAsync(owner, confId, $"content {word}\n");
        var intId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "internal" })).GetProperty("id").GetGuid();
        await UploadVersionAsync(owner, intId, $"content {word}\n");
        (await owner.PutAsJsonAsync($"/api/documents/{confId}/sensitivity", new { labelId = confidentialId })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/documents/{intId}/sensitivity", new { labelId = internalId })).EnsureSuccessStatusCode();

        // The child listing carries the label id + name per row (ADR "Configurable sensitivity labels + upload defaults").
        var children = (await TestJson.Get(owner, $"/api/documents/{repoId}/children")).GetProperty("children").EnumerateArray().ToList();
        Assert.Equal(confidentialId, children.Single(c => c.GetProperty("id").GetGuid() == confId).GetProperty("sensitivityLabelId").GetGuid());
        Assert.Equal("Confidential", children.Single(c => c.GetProperty("id").GetGuid() == confId).GetProperty("sensitivityLabelName").GetString());
        Assert.Equal("Internal", children.Single(c => c.GetProperty("id").GetGuid() == intId).GetProperty("sensitivityLabelName").GetString());

        // The search facet counts each label; drill-down narrows to the selected one.
        await PollAsync(async () =>
        {
            var facets = (await TestJson.Get(owner, $"/api/search?q={word}")).GetProperty("facets");
            return facets.ValueKind == JsonValueKind.Object
                && BucketCount(facets, "sensitivityLabels", "Confidential") == 1
                && BucketCount(facets, "sensitivityLabels", "Internal") == 1;
        }, "both labels faceted");

        var drilled = await SearchIdsAsync(owner, $"q={word}&system[sensitivityLabel][in]=Confidential");
        Assert.Contains(confId, drilled);
        Assert.DoesNotContain(intId, drilled);

        // Post-filter faceting: after selecting Confidential, its OWN dimension keeps the other value visible.
        var after = (await TestJson.Get(owner, $"/api/search?q={word}&system[sensitivityLabel][in]=Confidential")).GetProperty("facets");
        Assert.Equal(1, BucketCount(after, "sensitivityLabels", "Internal"));
    }

    private static long BucketCount(JsonElement facets, string group, string value) =>
        facets.TryGetProperty(group, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(b => b.GetProperty("value").GetString() == value).Select(b => b.GetProperty("count").GetInt64()).FirstOrDefault()
            : 0;

    private static async Task PollAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 30; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}");
    }
}
