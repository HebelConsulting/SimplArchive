using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising in-app notifications (ADR "Notifications (in-app, first
// slice)"): a ServiceAccount owner grants a User access (→ AccessGranted) and submits a version assigning that
// User as reviewer (→ ReviewAssigned); the reviewer reads their own intray, sees both, and marks them read.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class NotificationsTests
{
    private readonly E2EApiFactory _factory;

    public NotificationsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Grant_and_submit_notify_the_user_who_reads_and_marks_read()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"reviewer-{Guid.NewGuid():N}@e2e.local";
        const string password = "review1234";
        var reviewerId = await _factory.SeedUserAsync(tenantId, email, password, "Reviewer");

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Notif {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // A grant to the reviewer (a User) → an AccessGranted notification for them.
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{reviewerId}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "notif-doc" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        // Submitting for review, assigning the reviewer → a ReviewAssigned notification for them.
        await TestJson.Post(owner, $"/api/documents/{docId}/versions/{versionId}/workflow/submit", new { reviewerId });

        // The reviewer reads their own intray and sees both (unread).
        using var reviewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var intray = await TestJson.Get(reviewer, "/api/notifications");
        Assert.Equal(2, intray.GetProperty("unreadCount").GetInt32());
        var notifications = intray.GetProperty("notifications").EnumerateArray().ToList();
        Assert.Contains(notifications, n => n.GetProperty("type").GetString() == "AccessGranted");
        var review = notifications.Single(n => n.GetProperty("type").GetString() == "ReviewAssigned");
        Assert.Equal(docId, review.GetProperty("documentId").GetGuid());
        // The document's parent folder is exposed for click-through navigation (ADR "Notification viewer + click-through").
        Assert.Equal(repoId, review.GetProperty("documentParentId").GetGuid());
        Assert.False(review.GetProperty("isRead").GetBoolean());

        // Mark one read → unread drops to 1; mark-all read → 0.
        (await reviewer.PostAsync($"/api/notifications/{review.GetProperty("id").GetGuid()}/read", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, (await TestJson.Get(reviewer, "/api/notifications/unread-count")).GetProperty("unreadCount").GetInt32());
        (await reviewer.PostAsync("/api/notifications/read-all", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await TestJson.Get(reviewer, "/api/notifications/unread-count")).GetProperty("unreadCount").GetInt32());

        // The owner (a ServiceAccount) has no intray at all — the notifications endpoint is User-only.
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await owner.GetAsync("/api/notifications")).StatusCode);
    }

    // ADR "Notification digest / coalescing": a burst of comments on one document collapses into a single
    // notification for the document's author, carrying eventCount.
    [Fact]
    public async Task Multiple_comments_coalesce_into_one_notification_for_the_author()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var authorEmail = $"author-{Guid.NewGuid():N}@e2e.local";
        var commenterEmail = $"commenter-{Guid.NewGuid():N}@e2e.local";
        const string password = "digest1234";
        var authorId = await _factory.SeedUserAsync(tenantId, authorEmail, password, "Author");
        var commenterId = await _factory.SeedUserAsync(tenantId, commenterEmail, password, "Commenter");

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Digest {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        // Author may see + create under the repo; commenter may see + comment.
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{authorId}", new { canSee = true, canReadContent = true, canCreateSubItems = true })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{commenterId}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        using var author = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(authorEmail, password));
        var docId = (await TestJson.Post(author, $"/api/documents/{repoId}/children", new { name = "authored-doc" })).GetProperty("id").GetGuid();

        // The commenter posts three top-level comments → three ChatMessagePosted events for the author, coalesced.
        using var commenter = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(commenterEmail, password));
        for (var i = 0; i < 3; i++)
        {
            (await commenter.PostAsJsonAsync($"/api/documents/{docId}/chat", new { body = $"comment {i}" })).EnsureSuccessStatusCode();
        }

        // The three comments collapsed into a SINGLE ChatMessagePosted notification carrying eventCount 3 (the
        // author also has one AccessGranted notification from the earlier grant — a discrete, non-coalesced type).
        var intray = await TestJson.Get(author, "/api/notifications");
        var notifications = intray.GetProperty("notifications").EnumerateArray().ToList();
        var comment = notifications.Single(n => n.GetProperty("type").GetString() == "ChatMessagePosted");
        Assert.Equal(3, comment.GetProperty("eventCount").GetInt32());
        Assert.Single(notifications, n => n.GetProperty("type").GetString() == "AccessGranted");
        Assert.Equal(2, intray.GetProperty("unreadCount").GetInt32()); // the coalesced comment + the access grant
    }
}
