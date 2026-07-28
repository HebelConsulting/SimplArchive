using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for audit coverage of collaboration events (ADR "Audit collaboration events") over the real API +
// Postgres + object storage: posting a comment, adding + removing a reference, and creating + editing +
// deleting an annotation each land a stable action code in the tenant's audit log — the deferred second half
// of every-mutation coverage. Detail is action + a short summary only (no comment/note content), so the log
// stays PII-light.
[Collection(E2ECollection.Name)]
public class AuditCollaborationCoverageTests
{
    private readonly E2EApiFactory _factory;

    public AuditCollaborationCoverageTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Comment_reference_and_annotation_mutations_are_all_audited()
    {
        // An admin ServiceAccount (full-rights auto-grant on the repo it creates) performs every collaboration
        // action, then a User holding CanViewAuditLog reads the log.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Collab {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "collab-target" })).GetProperty("id").GetGuid();

        // Confirm a version so the document has a page to annotate.
        var version = await PostJson(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = version.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("body")))).EnsureSuccessStatusCode();
        }
        await PutJson(api, $"/api/documents/{docId}/versions/{versionId}", new { });

        // Comment.
        await PostJson(api, $"/api/documents/{docId}/comments", new { body = "A comment" });

        // Reference: add a shortcut to the document in the repo root, then remove it.
        var reference = await PostJson(api, $"/api/documents/{repoId}/references", new { targetId = docId });
        var referenceId = reference.GetProperty("referenceId").GetGuid();
        (await api.DeleteAsync($"/api/documents/{repoId}/references/{referenceId}")).EnsureSuccessStatusCode();

        // Annotation: create → edit (author + If-Match) → delete.
        var annotationsUrl = $"/api/documents/{docId}/versions/{versionId}/annotations";
        var note = await PostJson(api, annotationsUrl, new { pageIndex = 0, positionX = 0.2, positionY = 0.3, text = "note", color = "#FFEB3B" });
        var noteId = note.GetProperty("id").GetGuid();
        var etag = note.GetProperty("etag").GetString()!;
        var edited = await PutWithIfMatch(api, $"{annotationsUrl}/{noteId}", etag, new { pageIndex = 0, positionX = 0.4, positionY = 0.3, text = "note", color = "#8BC34A" });
        var newEtag = JsonSerializer.Deserialize<JsonElement>(await edited.Content.ReadAsStringAsync()).GetProperty("etag").GetString()!;
        (await DeleteWithIfMatch(api, $"{annotationsUrl}/{noteId}", newEtag)).EnsureSuccessStatusCode();

        // A User with CanViewAuditLog reads the log and finds every collaboration action code.
        var viewerEmail = $"collab-auditor-{Guid.NewGuid():N}@e2e.local";
        const string password = "collab1234";
        await _factory.SeedUserAsync(tenantId, viewerEmail, password, "Collab auditor", canViewAuditLog: true);
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, password));

        var actions = (await GetJson(viewer, "/api/audit-events?limit=200")).GetProperty("events")
            .EnumerateArray()
            .Select(e => e.GetProperty("action").GetString())
            .ToHashSet();

        Assert.Contains("Comment.Posted", actions);
        Assert.Contains("Reference.Added", actions);
        Assert.Contains("Reference.Removed", actions);
        Assert.Contains("Annotation.Added", actions);
        Assert.Contains("Annotation.Edited", actions);
        Assert.Contains("Annotation.Removed", actions);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> PutWithIfMatch(HttpClient api, string url, string etag, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        return api.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteWithIfMatch(HttpClient api, string url, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        return api.SendAsync(request);
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body) => await ReadJson(await client.PostAsJsonAsync(url, body));

    private static async Task<JsonElement> PutJson(HttpClient client, string url, object body) => await ReadJson(await client.PutAsJsonAsync(url, body));

    private static async Task<JsonElement> GetJson(HttpClient client, string url) => await ReadJson(await client.GetAsync(url));

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }

        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
