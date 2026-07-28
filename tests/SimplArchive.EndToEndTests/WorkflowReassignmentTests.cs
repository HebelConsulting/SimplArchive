using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API (in-process) + real Postgres + object storage, exercising workflow review
// reassignment (ADR "Workflow review reassignment"): a general reassign action (delegate/re-route an "In
// Review" task to a different reviewer) and the deactivation guard — deactivating a user who still holds
// pending reviews is refused unless a replacement reviewer is supplied to take them over.
[Collection(E2ECollection.Name)]
public class WorkflowReassignmentTests
{
    private readonly E2EApiFactory _factory;

    public WorkflowReassignmentTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Reassign_moves_the_task_and_only_the_new_reviewer_can_act()
    {
        var s = await SetUpAsync("Gamma");
        var b = await SeedReviewerAsync(s.TenantId, s.RepoId, s.Owner, "Bravo");

        // Owner (an editor) submits, assigning reviewer A.
        await TestJson.Post(s.Owner, $"{s.Workflow}/submit", new { reviewerId = s.ReviewerAId });

        // Reassigning to the same reviewer is a no-op error.
        var same = await s.Owner.PostAsJsonAsync($"{s.Workflow}/reassign", new { reviewerId = s.ReviewerAId });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Equal("INVALID_REVIEWER", await ErrorCodeAsync(same));

        // The editor re-routes the review from A to B.
        var reassigned = await TestJson.Post(s.Owner, $"{s.Workflow}/reassign", new { reviewerId = b.Id });
        Assert.Equal("In Review", reassigned.GetProperty("statusName").GetString());
        Assert.Equal("Reviewer Bravo", reassigned.GetProperty("assignedToName").GetString());

        // The task moved: B's inbox has it, A's no longer does.
        using var reviewerA = _factory.CreateAuthedClient(s.ReviewerAToken);
        using var reviewerB = _factory.CreateAuthedClient(b.Token);
        Assert.Contains((await TestJson.Get(reviewerB, "/api/tasks")).GetProperty("tasks").EnumerateArray(),
            t => t.GetProperty("versionId").GetGuid() == s.VersionId);
        Assert.DoesNotContain((await TestJson.Get(reviewerA, "/api/tasks")).GetProperty("tasks").EnumerateArray(),
            t => t.GetProperty("versionId").GetGuid() == s.VersionId);

        // A is no longer the reviewer and isn't an editor → can neither reassign nor decide.
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewerA.PostAsJsonAsync($"{s.Workflow}/reassign", new { reviewerId = s.ReviewerAId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewerA.PostAsJsonAsync($"{s.Workflow}/approve", new { })).StatusCode);

        // B (the new reviewer) approves.
        Assert.Equal("Approved", (await TestJson.Post(reviewerB, $"{s.Workflow}/approve", new { })).GetProperty("statusName").GetString());

        // The reassignment is in the history (In Review → In Review, assigned to B).
        var history = (await TestJson.Get(s.Owner, s.Workflow)).GetProperty("history").EnumerateArray().ToArray();
        Assert.Contains(history, h => h.GetProperty("fromStatusName").GetString() == "In Review"
            && h.GetProperty("toStatusName").GetString() == "In Review"
            && h.GetProperty("assignedToName").GetString() == "Reviewer Bravo");
    }

    [Fact]
    public async Task Deactivating_a_reviewer_requires_a_replacement_and_reassigns_their_tasks()
    {
        var s = await SetUpAsync("Delta");
        var b = await SeedReviewerAsync(s.TenantId, s.RepoId, s.Owner, "Echo");

        // An admin User (CanManageUsers) to perform the deactivation.
        var adminEmail = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string adminPassword = "admin1234";
        await _factory.SeedUserAsync(s.TenantId, adminEmail, adminPassword, "WF Admin", canManageUsers: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, adminPassword));

        // Reviewer A is assigned a pending review.
        await TestJson.Post(s.Owner, $"{s.Workflow}/submit", new { reviewerId = s.ReviewerAId });

        // Deactivating A without a replacement is refused.
        var refused = await admin.DeleteAsync($"/api/users/{s.ReviewerAId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("REVIEWER_HAS_PENDING_REVIEWS", await ErrorCodeAsync(refused));

        // An invalid replacement (unknown id) is rejected.
        var badReplacement = await admin.DeleteAsync($"/api/users/{s.ReviewerAId}?reassignReviewsTo={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, badReplacement.StatusCode);
        Assert.Equal("INVALID_REPLACEMENT_REVIEWER", await ErrorCodeAsync(badReplacement));

        // Handing the reviews to B deactivates A and moves the task.
        var ok = await admin.DeleteAsync($"/api/users/{s.ReviewerAId}?reassignReviewsTo={b.Id}");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using var reviewerB = _factory.CreateAuthedClient(b.Token);
        Assert.Contains((await TestJson.Get(reviewerB, "/api/tasks")).GetProperty("tasks").EnumerateArray(),
            t => t.GetProperty("versionId").GetGuid() == s.VersionId);

        // The workflow is still In Review, now assigned to B.
        Assert.Equal("Reviewer Echo", (await TestJson.Get(s.Owner, s.Workflow)).GetProperty("assignedToName").GetString());
    }

    // ---- setup ---------------------------------------------------------------------------------------------

    private sealed record Setup(HttpClient Owner, Guid TenantId, Guid RepoId, Guid DocumentId, Guid VersionId, Guid ReviewerAId, string ReviewerAToken)
    {
        public string Workflow => $"/api/documents/{DocumentId}/versions/{VersionId}/workflow";
    }

    private sealed record Reviewer(Guid Id, string Token);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
    }

    // Owner ServiceAccount + a repo + a confirmed-version document + reviewer A (read-granted on the repo).
    private async Task<Setup> SetUpAsync(string label)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"WF {label} {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"wf-doc-{label}" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes($"content {label}")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        var a = await SeedReviewerAsync(tenantId, repoId, owner, "Alpha");
        return new Setup(owner, tenantId, repoId, docId, versionId, a.Id, a.Token);
    }

    // Seeds a User, grants them read on the repo root (inherited by the document → an eligible reviewer), and
    // returns their id + an interactive-login token.
    private async Task<Reviewer> SeedReviewerAsync(Guid tenantId, Guid repoId, HttpClient owner, string label)
    {
        var email = $"reviewer-{label.ToLowerInvariant()}-{Guid.NewGuid():N}@e2e.local";
        const string password = "review1234";
        var id = await _factory.SeedUserAsync(tenantId, email, password, $"Reviewer {label}");
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{id}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();
        return new Reviewer(id, await _factory.GetUserTokenAsync(email, password));
    }
}
