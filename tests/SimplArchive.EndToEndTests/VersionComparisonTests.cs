using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end for document version comparison (ADR 0712) over the real API + Postgres + object storage
// (+ Tika): the endpoint returns the two versions' EXTRACTED TEXTS — the diff itself is the clients'
// (Presentation TextDiff, pinned by TextDiffTests); a binary version reports "not available"; a note-style
// .eml pair yields prose, not MIME; the caller needs CanReadContent.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class VersionComparisonTests
{
    private readonly E2EApiFactory _factory;

    public VersionComparisonTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Two_text_versions_return_both_extracted_texts()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Cmp {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "diff-doc" })).GetProperty("id").GetGuid();

        var v1 = await UploadVersionAsync(api, docId, ".txt", "alpha\nbeta\ngamma\n");
        var v2 = await UploadVersionAsync(api, docId, ".txt", "alpha\nBETA changed\ngamma\ndelta\n");

        var cmp = await TestJson.Get(api, $"/api/documents/{docId}/versions/compare?from={v1}&to={v2}");
        Assert.True(cmp.GetProperty("available").GetBoolean());
        Assert.Equal("alpha\nbeta\ngamma\n", cmp.GetProperty("fromText").GetString());
        Assert.Equal("alpha\nBETA changed\ngamma\ndelta\n", cmp.GetProperty("toText").GetString());
    }

    [Fact]
    public async Task An_eml_pair_compares_as_prose_not_mime()
    {
        // The headline case of #803: a note edited from a mail client is HTML in an .eml, and its versions
        // must compare as the words the user wrote — bodies extracted, tags stripped, envelope absent.
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Cmp {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "note-doc" })).GetProperty("id").GetGuid();

        const string emlV1 = "From: notes@e2e.local\r\nSubject: shopping\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<div>milk</div><div>bread</div>\r\n";
        const string emlV2 = "From: notes@e2e.local\r\nSubject: shopping\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<div>milk</div><div>bread &amp; butter</div>\r\n";
        var v1 = await UploadVersionAsync(api, docId, ".eml", emlV1);
        var v2 = await UploadVersionAsync(api, docId, ".eml", emlV2);

        var cmp = await TestJson.Get(api, $"/api/documents/{docId}/versions/compare?from={v1}&to={v2}");
        Assert.True(cmp.GetProperty("available").GetBoolean());
        var fromText = cmp.GetProperty("fromText").GetString()!;
        var toText = cmp.GetProperty("toText").GetString()!;
        Assert.Contains("milk", fromText);
        Assert.Contains("bread & butter", toText);        // entity decoded
        Assert.DoesNotContain("<div>", toText);           // tags stripped
        Assert.DoesNotContain("Content-Type", fromText);  // the envelope is not the note
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

        var cmp = await TestJson.Get(api, $"/api/documents/{docId}/versions/compare?from={v1}&to={v2}");
        Assert.False(cmp.GetProperty("available").GetBoolean());
        Assert.Equal(string.Empty, cmp.GetProperty("fromText").GetString());
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
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/documents/{docId}/versions/compare?from={v1}&to={v2}")).StatusCode);
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
