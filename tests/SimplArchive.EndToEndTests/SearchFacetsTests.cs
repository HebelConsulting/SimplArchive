using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + OpenSearch, exercising search facets (ADR "Search facets"): a search
// returns document-type / created-by / year facet counts over the result set, and clicking a document-type
// facet (system[documentType][eq]=…) drills the results down. Async indexing, so polled to consistency.
[Collection(E2ECollection.Name)]
public class SearchFacetsTests
{
    private readonly E2EApiFactory _factory;

    public SearchFacetsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Facets_are_returned_and_document_type_drills_down()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var mask1 = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 5);
        var mask2 = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 5);
        var (type1, type2) = (await MaskNameAsync(owner, mask1), await MaskNameAsync(owner, mask2));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Facets-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        const string word = "facettest";
        var docA = await SeedClassifiedDocAsync(owner, repoId, mask1, word);
        var docB = await SeedClassifiedDocAsync(owner, repoId, mask1, word);
        var docC = await SeedClassifiedDocAsync(owner, repoId, mask2, word);

        // Wait until all three are content-indexed.
        await PollAsync(async () =>
        {
            var ids = await SearchIdsAsync(owner, word);
            return ids.Contains(docA) && ids.Contains(docB) && ids.Contains(docC);
        }, "all three docs indexed");

        // The broad search's facets: document type (mask1 → 2, mask2 → 1), the 2021 year (all three), created-by.
        var facets = (await TestJson.Get(owner, $"/api/search?q={word}")).GetProperty("facets");
        Assert.Equal(2, BucketCount(facets, "documentTypes", type1));
        Assert.Equal(1, BucketCount(facets, "documentTypes", type2));
        Assert.Equal(3, BucketCount(facets, "years", "2021"));
        Assert.True(facets.GetProperty("createdBy").EnumerateArray().Sum(b => b.GetProperty("count").GetInt64()) >= 3);

        // Drill down by document type → only the two mask1 documents.
        var drilled = await SearchIdsAsync(owner, word, extra: $"&system[documentType][eq]={Uri.EscapeDataString(type1)}");
        Assert.Contains(docA, drilled);
        Assert.Contains(docB, drilled);
        Assert.DoesNotContain(docC, drilled);

        // File-type facet (ADR "Search facet refinements"): all three are .txt.
        Assert.Equal(3, BucketCount(facets, "fileTypes", "txt"));

        // Post-filter faceting: after selecting document type type1, its OWN dimension stays open (type2 still
        // shows), while the count reflects only the selected-dimension-excluded context.
        var afterType1 = (await TestJson.Get(owner, $"/api/search?q={word}&system[documentType][in]={Uri.EscapeDataString(type1)}")).GetProperty("facets");
        Assert.Equal(1, BucketCount(afterType1, "documentTypes", type2)); // type2 still visible (dimension not collapsed)

        // Multi-select OR within a dimension → both types' documents.
        var both = await SearchIdsAsync(owner, word, extra: $"&system[documentType][in]={Uri.EscapeDataString(type1)},{Uri.EscapeDataString(type2)}");
        Assert.Contains(docA, both);
        Assert.Contains(docC, both);
    }

    [Fact]
    public async Task Index_field_facet_is_returned_and_drills_down()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var (maskId, fieldId) = await _factory.SeedMaskWithSelectFieldAsync(tenantId, "Vendor");
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"FieldFacets-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        const string word = "vendortest";

        var acme = await SeedFieldDocAsync(owner, repoId, maskId, fieldId, "Acme", word);
        var globex = await SeedFieldDocAsync(owner, repoId, maskId, fieldId, "Globex", word);

        await PollAsync(async () =>
        {
            var ids = await SearchIdsAsync(owner, word);
            return ids.Contains(acme) && ids.Contains(globex);
        }, "both field docs indexed");

        // The response carries a per-field facet for the "Vendor" Select field with the two values.
        await PollAsync(async () =>
        {
            var facets = (await TestJson.Get(owner, $"/api/search?q={word}")).GetProperty("facets");
            var vendor = facets.GetProperty("fields").EnumerateArray().FirstOrDefault(f => f.GetProperty("name").GetString() == "Vendor");
            return vendor.ValueKind == JsonValueKind.Object
                && FieldBucketCount(vendor, "Acme") == 1 && FieldBucketCount(vendor, "Globex") == 1;
        }, "Vendor field facet populated");

        // Drill down by the field value → only the Acme document.
        var drilled = await SearchIdsAsync(owner, word, extra: "&fields[Vendor][in]=Acme");
        Assert.Contains(acme, drilled);
        Assert.DoesNotContain(globex, drilled);
    }

    private static long FieldBucketCount(JsonElement fieldFacet, string value) =>
        fieldFacet.GetProperty("buckets").EnumerateArray().Where(b => b.GetProperty("value").GetString() == value)
            .Select(b => b.GetProperty("count").GetInt64()).FirstOrDefault();

    private static async Task<Guid> SeedFieldDocAsync(HttpClient owner, Guid repoId, Guid maskId, Guid fieldId, string value, string content)
    {
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt", documentDate = "2022-03-01" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        // Fill the index-field value first, then assign the mask (required-field ordering).
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/index-data", new { fields = new[] { new { fieldDefinitionId = fieldId, fieldName = "Vendor", values = new[] { value } } } })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();
        return docId;
    }

    private static long BucketCount(JsonElement facets, string group, string value) =>
        facets.GetProperty(group).EnumerateArray().Where(b => b.GetProperty("value").GetString() == value)
            .Select(b => b.GetProperty("count").GetInt64()).FirstOrDefault();

    private static async Task<string> MaskNameAsync(HttpClient owner, Guid maskId) =>
        (await TestJson.Get(owner, "/api/masks")).GetProperty("masks").EnumerateArray()
            .Single(m => m.GetProperty("id").GetGuid() == maskId).GetProperty("name").GetString()!;

    private static async Task<Guid> SeedClassifiedDocAsync(HttpClient owner, Guid repoId, Guid maskId, string content)
    {
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt", documentDate = "2021-06-01" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();
        return docId;
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
