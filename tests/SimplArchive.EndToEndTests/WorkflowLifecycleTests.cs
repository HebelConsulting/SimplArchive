using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API (in-process) + real Postgres + MinIO, exercising the approval workflow
// (ADR "Workflow engine slice 1", 0298) with a real interactive-login User reviewer — the reviewer decision
// (approve/reject) is only ever a User, so this drives the actual OAuth2 Authorization Code + PKCE flow rather
// than a client-credentials ServiceAccount token. Covers submit → task inbox → approve → release, the reject
// path (mandatory reason), and that only the assigned reviewer can decide.
[Collection(E2ECollection.Name)]
public class WorkflowLifecycleTests
{
    private readonly E2EApiFactory _factory;

    public WorkflowLifecycleTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Submit_task_inbox_approve_release_happy_path()
    {
        var setup = await SetUpConfirmedVersionWithReviewerAsync("Alpha");
        var workflow = setup.WorkflowPath;
        using var reviewer = _factory.CreateAuthedClient(setup.ReviewerToken);

        // Draft (no workflow row) yet.
        Assert.Equal("Draft", (await TestJson.Get(setup.Owner, workflow)).GetProperty("statusName").GetString());

        // Owner (ServiceAccount, CanEditContent via the repo auto-grant) submits, assigning the reviewer.
        var submitted = await TestJson.Post(setup.Owner, $"{workflow}/submit", new { reviewerId = setup.ReviewerId });
        Assert.Equal("In Review", submitted.GetProperty("statusName").GetString());

        // The reviewer's task inbox now shows the version.
        var tasks = (await TestJson.Get(reviewer, "/api/tasks")).GetProperty("tasks");
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("versionId").GetGuid() == setup.VersionId);

        // Only the assigned reviewer may decide — the owner cannot approve.
        var ownerApprove = await setup.Owner.PostAsJsonAsync($"{workflow}/approve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, ownerApprove.StatusCode);

        // Reviewer approves → Approved; owner releases → Released.
        Assert.Equal("Approved", (await TestJson.Post(reviewer, $"{workflow}/approve", new { })).GetProperty("statusName").GetString());
        Assert.Equal("Released", (await TestJson.Post(setup.Owner, $"{workflow}/release", new { })).GetProperty("statusName").GetString());

        // The task inbox is empty once resolved (assignment cleared).
        var tasksAfter = (await TestJson.Get(reviewer, "/api/tasks")).GetProperty("tasks");
        Assert.DoesNotContain(tasksAfter.EnumerateArray(), t => t.GetProperty("versionId").GetGuid() == setup.VersionId);

        // Full history recorded in order.
        var history = (await TestJson.Get(setup.Owner, workflow)).GetProperty("history")
            .EnumerateArray().Select(h => h.GetProperty("toStatusName").GetString()).ToArray();
        Assert.Equal(new[] { "In Review", "Approved", "Released" }, history);
    }

    [Fact]
    public async Task Reject_requires_a_reason_and_records_it()
    {
        var setup = await SetUpConfirmedVersionWithReviewerAsync("Beta");
        var workflow = setup.WorkflowPath;
        using var reviewer = _factory.CreateAuthedClient(setup.ReviewerToken);

        await TestJson.Post(setup.Owner, $"{workflow}/submit", new { reviewerId = setup.ReviewerId });

        // A blank reason is rejected.
        var blank = await reviewer.PostAsJsonAsync($"{workflow}/reject", new { reason = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        // With a reason → Rejected, and the reason is recorded on the transition.
        var rejected = await TestJson.Post(reviewer, $"{workflow}/reject", new { reason = "Totals don't add up." });
        Assert.Equal("Rejected", rejected.GetProperty("statusName").GetString());

        var lastTransition = (await TestJson.Get(setup.Owner, workflow)).GetProperty("history").EnumerateArray().Last();
        Assert.Equal("Rejected", lastTransition.GetProperty("toStatusName").GetString());
        Assert.Equal("Totals don't add up.", lastTransition.GetProperty("rejectionReason").GetString());

        // Rejected can be resubmitted (Rejected → In Review).
        var resubmitted = await TestJson.Post(setup.Owner, $"{workflow}/submit", new { reviewerId = setup.ReviewerId });
        Assert.Equal("In Review", resubmitted.GetProperty("statusName").GetString());
    }

    // ---- setup ---------------------------------------------------------------------------------------------

    private sealed record Setup(HttpClient Owner, Guid DocumentId, Guid VersionId, Guid ReviewerId, string ReviewerToken)
    {
        public string WorkflowPath => $"/api/documents/{DocumentId}/versions/{VersionId}/workflow";
    }

    // Creates a repository (owned by a ServiceAccount with full rights), grants a freshly-seeded User read access
    // on it (inherited by the document → makes them an eligible reviewer), then creates a document with one
    // confirmed version. Returns the owner client + a real interactive-login token for the reviewer.
    private async Task<Setup> SetUpConfirmedVersionWithReviewerAsync(string label)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"reviewer-{Guid.NewGuid():N}@e2e.local";
        const string password = "review1234";
        var reviewerId = await _factory.SeedUserAsync(tenantId, email, password, $"Reviewer {label}");

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"WF {label} {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // Grant the reviewer read on the repo root; the document inherits it, so they can be assigned + read it.
        var grant = await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{reviewerId}", new { canSee = true, canReadContent = true });
        grant.EnsureSuccessStatusCode();

        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"wf-doc-{label}" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes($"content {label}")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        var reviewerToken = await _factory.GetUserTokenAsync(email, password);
        return new Setup(owner, docId, versionId, reviewerId, reviewerToken);
    }
}
