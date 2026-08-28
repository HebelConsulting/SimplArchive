using System.Text;

namespace SimplArchive.EndToEndTests;

// The download-link contract both clients depend on (ADR 0218/0259/0277): a confirmed version exposes a
// `download` presigned link that returns the exact original bytes AND sets Content-Disposition to the
// document's name stem + the version's file extension — the desktop "Open"/"Save as…" and the web download
// both rely on this, so it's asserted once here at the API level.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentDownloadTests
{
    private readonly E2EApiFactory _factory;

    public DocumentDownloadTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Download_link_returns_exact_bytes_and_a_stem_plus_extension_filename()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"dl-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        // Name is the bare stem (ADR 0277); the extension is a per-version property carried on the object key.
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "report" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();

        var content = Encoding.UTF8.GetBytes("download me, byte for byte");
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(content))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });

        var version = await TestJson.Get(api, $"/api/documents/{docId}/versions/{versionId}");
        var downloadUrl = version.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()!;

        // Fetch the presigned URL directly (the API never proxies bytes) and assert both halves of the contract.
        using var http = new HttpClient();
        using var response = await http.GetAsync(downloadUrl);
        response.EnsureSuccessStatusCode();

        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());

        // MinIO echoes the signed response-content-disposition. Filename = name stem + version extension.
        var contentDisposition = response.Content.Headers.ContentDisposition?.ToString() ?? "";
        Assert.Contains("report.txt", contentDisposition);
    }
}
