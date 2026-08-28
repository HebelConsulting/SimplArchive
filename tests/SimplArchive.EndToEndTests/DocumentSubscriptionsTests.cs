using System.Net;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising document subscriptions (ADR "Document subscriptions"):
// a user follows a document, a new version by someone else produces a SubscribedActivity notification, and
// after unfollowing the next change is silent. A ServiceAccount can't subscribe (no in-app intray).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentSubscriptionsTests
{
    private readonly E2EApiFactory _factory;

    public DocumentSubscriptionsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Following_a_document_notifies_the_follower_of_a_new_version_until_they_unfollow()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A follower who can see the document (tenant admin → CanSee via the IsTenantAdmin bypass).
        var email = $"follower-{Guid.NewGuid():N}@e2e.local";
        const string password = "follow-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Follower");
        await _factory.GrantTenantAdminAsync(email);
        using var follower = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Sub {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "sub-doc" })).GetProperty("id").GetGuid();
        await AddVersionAsync(owner, docId);

        // A ServiceAccount can't subscribe (no in-app intray).
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsync($"/api/documents/{docId}/subscription", null)).StatusCode);

        // The follower subscribes.
        (await follower.PutAsync($"/api/documents/{docId}/subscription", null)).EnsureSuccessStatusCode();
        Assert.True((await TestJson.Get(follower, $"/api/documents/{docId}/subscription")).GetProperty("subscribed").GetBoolean());

        // The owner adds a new version → the follower gets a SubscribedActivity notification for the document.
        await AddVersionAsync(owner, docId);
        var afterFollow = (await TestJson.Get(follower, "/api/notifications")).GetProperty("notifications").EnumerateArray()
            .Count(n => n.GetProperty("type").GetString() == "SubscribedActivity" && n.GetProperty("documentId").GetGuid() == docId);
        Assert.Equal(1, afterFollow);

        // The follower unsubscribes → a further new version produces no new SubscribedActivity notification.
        (await follower.DeleteAsync($"/api/documents/{docId}/subscription")).EnsureSuccessStatusCode();
        Assert.False((await TestJson.Get(follower, $"/api/documents/{docId}/subscription")).GetProperty("subscribed").GetBoolean());

        await AddVersionAsync(owner, docId);
        var afterUnfollow = (await TestJson.Get(follower, "/api/notifications")).GetProperty("notifications").EnumerateArray()
            .Count(n => n.GetProperty("type").GetString() == "SubscribedActivity" && n.GetProperty("documentId").GetGuid() == docId);
        Assert.Equal(1, afterUnfollow); // unchanged
    }

    [Fact]
    public async Task Following_a_folder_notifies_of_activity_anywhere_in_its_subtree()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"folfollower-{Guid.NewGuid():N}@e2e.local";
        const string password = "follow-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "FolderFollower");
        await _factory.GrantTenantAdminAsync(email);
        using var follower = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Sub {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "watched-folder" })).GetProperty("id").GetGuid();

        // The follower follows the FOLDER (a folder is a Document, so the same subscription endpoint applies).
        (await follower.PutAsync($"/api/documents/{folderId}/subscription", null)).EnsureSuccessStatusCode();

        // A brand-new document is filed inside the folder → the folder-follower is notified (implicit subtree).
        var leafId = (await TestJson.Post(owner, $"/api/documents/{folderId}/children", new { name = "subtree-doc" })).GetProperty("id").GetGuid();
        await AddVersionAsync(owner, leafId);

        var afterFile = (await TestJson.Get(follower, "/api/notifications")).GetProperty("notifications").EnumerateArray()
            .Count(n => n.GetProperty("type").GetString() == "SubscribedActivity" && n.GetProperty("documentId").GetGuid() == leafId);
        Assert.True(afterFile >= 1); // notified about the new document deep in the followed folder
    }

    private static async Task AddVersionAsync(HttpClient client, Guid docId)
    {
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes($"v-{Guid.NewGuid():N}")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
    }
}
