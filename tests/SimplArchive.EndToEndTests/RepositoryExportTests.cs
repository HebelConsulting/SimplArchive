using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + MinIO, exercising repository export (ADR "Repository export"): a
// tenant admin exports a repository subtree to a .zip; the archive carries the manifest + document/version
// metadata and the version blob round-trips byte-for-byte from object storage. A non-admin is refused.
[Collection(E2ECollection.Name)]
public class RepositoryExportTests
{
    private readonly E2EApiFactory _factory;

    public RepositoryExportTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Tenant_admin_exports_a_subtree_to_a_zip_and_the_blob_round_trips()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "export-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Repo → folder → document with a confirmed version (a stored blob to pack).
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Export {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Folder" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{folderId}/children", new { name = "Report" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var payload = Encoding.UTF8.GetBytes($"export-content-{Guid.NewGuid():N}");
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(payload))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        // A non-tenant-admin (the service-account owner) is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/documents/{repoId}/export?versions=all")).StatusCode);

        // The tenant admin downloads the archive.
        var response = await admin.GetAsync($"/api/documents/{repoId}/export?versions=all");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        var zipBytes = await response.Content.ReadAsByteArrayAsync();

        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);

        // Manifest is present with a sane format version + counts.
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestReader = new StreamReader(manifestEntry!.Open());
        var manifest = JsonDocument.Parse(await manifestReader.ReadToEndAsync()).RootElement;
        Assert.Equal(2, manifest.GetProperty("formatVersion").GetInt32()); // 2 since the chat rename (#382)
        Assert.Equal(1, manifest.GetProperty("counts").GetProperty("versions").GetInt32());
        Assert.Equal(3, manifest.GetProperty("counts").GetProperty("documents").GetInt32()); // repo, folder, report

        // The exported document tree names Report.
        var docsEntry = archive.GetEntry("tree/documents.jsonl")!;
        using var docsReader = new StreamReader(docsEntry.Open());
        Assert.Contains("Report", await docsReader.ReadToEndAsync());

        // The single content-addressed blob round-trips byte-for-byte.
        var blob = Assert.Single(archive.Entries, e => e.FullName.StartsWith("blobs/"));
        using var blobStream = new MemoryStream();
        await using (var open = blob.Open())
        {
            await open.CopyToAsync(blobStream);
        }

        Assert.Equal(payload, blobStream.ToArray());
    }
}
