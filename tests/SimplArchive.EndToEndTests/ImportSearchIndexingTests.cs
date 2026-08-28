using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.EndToEndTests;

// Regression for a real gap (ADR 0526/0527): RepositoryImporter created documents in the database but never
// enqueued them for search indexing, so imported documents were invisible to full-text search. Export a repo,
// re-import it, and assert the imported copy is findable by name.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ImportSearchIndexingTests
{
    private readonly E2EApiFactory _factory;

    public ImportSearchIndexingTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Imported_documents_are_searchable()
    {
        var word = $"wischermotor{Guid.NewGuid():N}"; // a distinctive token used only in the document name

        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        await GrantImportExportAsync(clientId); // export + import both need CanExport/CanImport
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A repo → document whose NAME carries the token, with a confirmed version.
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"ISI {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = $"Rechnung {word}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("body")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(api, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        // Export the repo to a zip (with permissions, so the creator's ACL travels and the imported copy is visible).
        var exportResponse = await api.GetAsync($"/api/documents/{repoId}/export?versions=all&includePermissions=true");
        exportResponse.EnsureSuccessStatusCode();
        var zip = await exportResponse.Content.ReadAsByteArrayAsync();

        // Import it back (a fresh repository).
        using (var content = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(zip);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Add(file, "file", "import.zip");
            var import = await api.PostAsync("/api/repositories/import?includePermissions=true", content);
            import.EnsureSuccessStatusCode();
        }

        // The imported copy is findable by its name token — there are now two matches (original + imported).
        await PollAsync(async () => (await SearchIdsAsync(api, word)).Count >= 2, "imported document indexed for search");
    }

    private async Task GrantImportExportAsync(string clientId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.SimplArchiveDbContext>();
        var sa = await db.ServiceAccounts.IgnoreQueryFilters().SingleAsync(s => s.OpenIddictApplicationClientId == clientId);
        sa.CanImport = true;
        sa.CanExport = true;
        await db.SaveChangesAsync();
    }

    private static async Task<HashSet<Guid>> SearchIdsAsync(HttpClient client, string q)
    {
        var response = await TestJson.Get(client, $"/api/search?q={Uri.EscapeDataString(q)}");
        return response.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
    }

    private static async Task PollAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }

        Assert.Fail($"Timed out waiting for: {what}");
    }
}
