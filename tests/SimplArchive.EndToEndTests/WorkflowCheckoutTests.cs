using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage, exercising the workflow × check-out interaction
// (ADR "Workflow / check-out interaction"): a checked-out document is mid-edit, so submitting it for review is
// refused (409 DOCUMENT_CHECKED_OUT) until the check-out is resolved — regardless of who holds the lock. Once
// released, submit succeeds; and checking out a document that is already In Review stays allowed (a new version
// doesn't touch the reviewed one).
[Collection(E2ECollection.Name)]
public class WorkflowCheckoutTests
{
    private readonly E2EApiFactory _factory;

    public WorkflowCheckoutTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Checked_out_document_cannot_be_submitted_until_released()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"WF-CO {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "wf-co-doc" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("check me out")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        // An editor User (CanEditContent — can both check out and submit) and a reviewer target.
        var editor = await SeedUserAsync(tenantId, repoId, owner, "Editor", canEditContent: true);
        var reviewer = await SeedUserAsync(tenantId, repoId, owner, "Reviewer", canEditContent: false);
        using var editorClient = _factory.CreateAuthedClient(editor.Token);

        var workflow = $"/api/documents/{docId}/versions/{versionId}/workflow";

        // The editor checks the document out (a real edit lock).
        (await editorClient.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();

        // Submitting it for review is now refused — even for the person holding the lock.
        var blocked = await editorClient.PostAsJsonAsync($"{workflow}/submit", new { reviewerId = reviewer.Id });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("DOCUMENT_CHECKED_OUT", await ErrorCodeAsync(blocked));

        // Releasing the check-out unblocks the submit.
        (await editorClient.DeleteAsync($"/api/documents/{docId}/checkout")).EnsureSuccessStatusCode();
        Assert.Equal("In Review", (await TestJson.Post(editorClient, $"{workflow}/submit", new { reviewerId = reviewer.Id })).GetProperty("statusName").GetString());

        // Checking the document out again *during* review stays allowed (a check-in makes a new version; the
        // reviewed one is untouched).
        (await editorClient.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();
    }

    private sealed record SeededUser(Guid Id, string Token);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
    }

    private async Task<SeededUser> SeedUserAsync(Guid tenantId, Guid repoId, HttpClient owner, string label, bool canEditContent)
    {
        var email = $"{label.ToLowerInvariant()}-{Guid.NewGuid():N}@e2e.local";
        const string password = "wfco1234";
        var id = await _factory.SeedUserAsync(tenantId, email, password, $"WF-CO {label}");
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{id}",
            new { canSee = true, canReadContent = true, canEditContent })).EnsureSuccessStatusCode();
        return new SeededUser(id, await _factory.GetUserTokenAsync(email, password));
    }
}
