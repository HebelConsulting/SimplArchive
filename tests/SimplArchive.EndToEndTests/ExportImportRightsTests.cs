using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API, exercising the dedicated CanExport/CanImport rights (ADR "Dedicated
// CanExport/CanImport rights"): export/import are gated on those specific rights (not tenant-admin any more),
// so a plain non-admin user granted CanExport can export and one granted CanImport can import — while a user
// holding neither is refused. Proves the capability can be delegated without full admin.
[Collection(E2ECollection.Name)]
public class ExportImportRightsTests
{
    private readonly E2EApiFactory _factory;

    public ExportImportRightsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Export_and_import_are_gated_on_the_dedicated_rights_and_delegable_to_non_admins()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "eir-1234";
        // A plain (non-admin) user granted only CanExport, another granted only CanImport, and one with neither.
        var exporterEmail = $"exporter-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, exporterEmail, password, "Exporter", canExport: true);
        using var exporter = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(exporterEmail, password));

        var importerEmail = $"importer-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, importerEmail, password, "Importer", canImport: true);
        using var importer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(importerEmail, password));

        var nobodyEmail = $"nobody-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, nobodyEmail, password, "Nobody");
        using var nobody = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(nobodyEmail, password));

        // A repo → document with a confirmed version (a blob to pack).
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"EIR {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Report" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes($"eir-{Guid.NewGuid():N}")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        // Export: the CanExport holder succeeds; the importer (no CanExport) and the no-rights user are refused.
        var exportResponse = await exporter.GetAsync($"/api/documents/{repoId}/export?versions=all");
        exportResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", exportResponse.Content.Headers.ContentType!.MediaType);
        var zip = await exportResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await importer.GetAsync($"/api/documents/{repoId}/export?versions=all")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync($"/api/documents/{repoId}/export?versions=all")).StatusCode);

        // Import (as a new repository): the CanImport holder succeeds; the exporter (no CanImport) and the
        // no-rights user are refused.
        using (var refused = MultipartOf(zip))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await exporter.PostAsync("/api/repositories/import", refused)).StatusCode);
        }
        using (var refused = MultipartOf(zip))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsync("/api/repositories/import", refused)).StatusCode);
        }
        using (var content = MultipartOf(zip))
        {
            (await importer.PostAsync("/api/repositories/import", content)).EnsureSuccessStatusCode();
        }
    }

    private static MultipartFormDataContent MultipartOf(byte[] zip)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "import.zip");
        return content;
    }
}
