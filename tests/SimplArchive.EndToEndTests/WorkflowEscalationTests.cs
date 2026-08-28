using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the review SLA / escalation flow (ADR "Workflow
// escalation / SLA reminders"): a document with a mask that has a 0-day SLA becomes overdue the moment it's
// submitted; the workflow + task resources report dueAt/isOverdue, and the escalation sweep notifies the
// reviewer.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class WorkflowEscalationTests
{
    private readonly E2EApiFactory _factory;

    public WorkflowEscalationTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Submitting_a_mask_with_an_sla_sets_a_deadline_and_the_sweep_escalates_when_overdue()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"reviewer-{Guid.NewGuid():N}@e2e.local";
        const string password = "review1234";
        var reviewerId = await _factory.SeedUserAsync(tenantId, email, password, "Reviewer");
        var maskId = await _factory.SeedMaskWithSlaAsync(tenantId, reviewSlaDays: 0); // due immediately

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Esc {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{reviewerId}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "esc-doc" })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        await TestJson.Post(owner, $"/api/documents/{docId}/versions/{versionId}/workflow/submit", new { reviewerId });

        // The workflow resource carries the deadline and reports overdue (the 0-day SLA means due = submit time).
        var workflow = await TestJson.Get(owner, $"/api/documents/{docId}/versions/{versionId}/workflow");
        Assert.False(workflow.GetProperty("dueAt").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.True(workflow.GetProperty("isOverdue").GetBoolean());

        // The reviewer's task also reports the deadline + overdue.
        using var reviewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var task = (await TestJson.Get(reviewer, "/api/tasks")).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("documentId").GetGuid() == docId);
        Assert.True(task.GetProperty("isOverdue").GetBoolean());

        // The escalation sweep notifies the reviewer that the review is overdue.
        await _factory.RunEscalationSweepAsync();
        var notifications = (await TestJson.Get(reviewer, "/api/notifications")).GetProperty("notifications").EnumerateArray().ToList();
        Assert.Contains(notifications, n => n.GetProperty("type").GetString() == "ReviewOverdue" && n.GetProperty("documentId").GetGuid() == docId);
    }
}
