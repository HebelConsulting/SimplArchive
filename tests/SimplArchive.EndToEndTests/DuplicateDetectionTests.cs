using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end for duplicate detection by content hash (ADR "Duplicate document detection") over the real API +
// Postgres + object storage: GET /api/duplicates?hash= finds tenant documents whose latest confirmed version is
// byte-identical, matches on the CURRENT version only, and never reveals a document the caller can't see.
[Collection(E2ECollection.Name)]
public class DuplicateDetectionTests
{
    private readonly E2EApiFactory _factory;

    public DuplicateDetectionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Finds_current_content_duplicates_across_the_tenant()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Dup {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        const string content = "identical duplicate content\n";

        var (aId, hash) = await UploadAsync(api, repoId, "doc-a", content);

        // The hash finds document A (with its path).
        var dupA = (await TestJson.Get(api, $"/api/duplicates?hash={hash}")).GetProperty("duplicates").EnumerateArray().ToList();
        Assert.Contains(dupA, d => d.GetProperty("id").GetGuid() == aId);
        Assert.All(dupA, d => Assert.False(string.IsNullOrEmpty(d.GetProperty("path").GetString())));

        // A non-matching hash → no duplicates.
        Assert.Empty((await TestJson.Get(api, $"/api/duplicates?hash={new string('a', 64)}")).GetProperty("duplicates").EnumerateArray());

        // Upload a second document with the SAME content → the hash now finds both.
        var (bId, _) = await UploadAsync(api, repoId, "doc-b", content);
        var both = (await TestJson.Get(api, $"/api/duplicates?hash={hash}")).GetProperty("duplicates").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(aId, both);
        Assert.Contains(bId, both);

        // Give A a NEW version with different content → A's CURRENT content no longer matches; only B remains.
        await AddVersionAsync(api, aId, "changed content\n");
        var afterChange = (await TestJson.Get(api, $"/api/duplicates?hash={hash}")).GetProperty("duplicates").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(aId, afterChange);
        Assert.Contains(bId, afterChange);

        // A caller who can't see B is not told about it.
        var (outsiderClientId, outsiderSecret) = await _factory.SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: false);
        using var outsider = _factory.CreateAuthedClient(await _factory.GetTokenAsync(outsiderClientId, outsiderSecret));
        Assert.Empty((await TestJson.Get(outsider, $"/api/duplicates?hash={hash}")).GetProperty("duplicates").EnumerateArray());
    }

    private static async Task<(Guid DocId, string Hash)> UploadAsync(HttpClient api, Guid repoId, string name, string content)
    {
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name })).GetProperty("id").GetGuid();
        var hash = await AddVersionAsync(api, docId, content);
        return (docId, hash);
    }

    private static async Task<string> AddVersionAsync(HttpClient api, Guid docId, string content)
    {
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        var finalized = await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        return finalized.GetProperty("sha256Hash").GetString()!;
    }
}
