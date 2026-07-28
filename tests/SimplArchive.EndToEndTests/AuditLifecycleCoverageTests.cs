using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the document content/metadata lifecycle audit coverage
// (ADR "Audit every-mutation coverage — document lifecycle"): creating a repository/document, adding a version,
// renaming, assigning/clearing a mask, editing index data, and changing the document date each append an audit
// event, readable through the CanViewAuditLog-gated log.
[Collection(E2ECollection.Name)]
public class AuditLifecycleCoverageTests
{
    private readonly E2EApiFactory _factory;

    public AuditLifecycleCoverageTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Document_lifecycle_mutations_are_all_audited()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var maskId = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 5);

        // Repository → document → confirmed version → rename → mask assign → index-data → document-date → mask clear.
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Audit {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "audit-doc" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("audit me")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        var etag = (await owner.GetAsync($"/api/documents/{docId}")).Headers.ETag!.Tag;
        using (var rename = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}") { Content = JsonContent.Create(new { name = "audit-doc-renamed" }) })
        {
            rename.Headers.TryAddWithoutValidation("If-Match", etag);
            (await owner.SendAsync(rename)).EnsureSuccessStatusCode();
        }

        (await owner.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/index-data", new { fields = Array.Empty<object>() })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/document-date", new { documentDate = "2020-01-02" })).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/documents/{docId}/mask")).EnsureSuccessStatusCode();

        // A CanViewAuditLog user reads the log; every lifecycle action is present.
        var viewerEmail = $"auditor-{Guid.NewGuid():N}@e2e.local";
        const string password = "audit-1234";
        await _factory.SeedUserAsync(tenantId, viewerEmail, password, "Auditor", canViewAuditLog: true);
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, password));

        var actions = (await TestJson.Get(viewer, "/api/audit-events?limit=200")).GetProperty("events")
            .EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToHashSet();

        foreach (var expected in new[]
        {
            "Repository.Created", "Document.Created", "Document.VersionAdded", "Document.Renamed",
            "Document.MaskAssigned", "Document.IndexDataUpdated", "Document.DocumentDateChanged", "Document.MaskCleared",
        })
        {
            Assert.Contains(expected, actions);
        }
    }
}
