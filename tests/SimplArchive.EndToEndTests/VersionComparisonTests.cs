using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end for document version comparison (ADR "Document version comparison") over the real API + Postgres +
// object storage (+ Tika): two text versions produce an inline unified diff (added/removed/unchanged lines); a
// binary version reports "not available"; the caller needs CanReadContent.
[Collection(E2ECollection.Name)]
public class VersionComparisonTests
{
    private readonly E2EApiFactory _factory;

    public VersionComparisonTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Two_text_versions_produce_an_inline_diff()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Cmp {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "diff-doc" })).GetProperty("id").GetGuid();

        var v1 = await UploadVersionAsync(api, docId, ".txt", "alpha\nbeta\ngamma\n");
        var v2 = await UploadVersionAsync(api, docId, ".txt", "alpha\nBETA changed\ngamma\ndelta\n");

        var cmp = await TestJson.Get(api, $"/api/documents/{docId}/versions/{v1}/compare/{v2}");
        Assert.True(cmp.GetProperty("available").GetBoolean());
        var lines = cmp.GetProperty("lines").EnumerateArray().Select(l => (Op: l.GetProperty("op").GetInt32(), Text: l.GetProperty("text").GetString())).ToList();

        // "alpha" + "gamma" unchanged (op 0); "beta" removed (op 2); "BETA changed" + "delta" added (op 1).
        Assert.Contains(lines, l => l.Op == 0 && l.Text == "alpha");
        Assert.Contains(lines, l => l.Op == 2 && l.Text == "beta");
        Assert.Contains(lines, l => l.Op == 1 && l.Text == "BETA changed");
        Assert.Contains(lines, l => l.Op == 1 && l.Text == "delta");
    }

    [Fact]
    public async Task A_binary_version_reports_not_available()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Cmp {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "bin-doc" })).GetProperty("id").GetGuid();

        var v1 = await UploadVersionAsync(api, docId, ".txt", "hello\n");
        // Random non-text bytes with a binary extension → no extractable text.
        var v2 = await UploadBytesVersionAsync(api, docId, ".bin", [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x7A, 0x13, 0x37]);

        var cmp = await TestJson.Get(api, $"/api/documents/{docId}/versions/{v1}/compare/{v2}");
        Assert.False(cmp.GetProperty("available").GetBoolean());
        Assert.Empty(cmp.GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public async Task Requires_read_content()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Cmp {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "acl-doc" })).GetProperty("id").GetGuid();
        var v1 = await UploadVersionAsync(owner, docId, ".txt", "a\n");
        var v2 = await UploadVersionAsync(owner, docId, ".txt", "b\n");

        // A service account with no grants can't compare.
        var (otherClientId, otherSecret) = await _factory.SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: false);
        using var outsider = _factory.CreateAuthedClient(await _factory.GetTokenAsync(otherClientId, otherSecret));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/documents/{docId}/versions/{v1}/compare/{v2}")).StatusCode);
    }

    private static Task<Guid> UploadVersionAsync(HttpClient api, Guid docId, string extension, string content) =>
        UploadBytesVersionAsync(api, docId, extension, Encoding.UTF8.GetBytes(content));

    private static async Task<Guid> UploadBytesVersionAsync(HttpClient api, Guid docId, string extension, byte[] bytes)
    {
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = extension });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        return versionId;
    }
}
