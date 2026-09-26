using System.Linq;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// Two escalation sweeps at once escalate an overdue review ONCE (#1425, tranche 2).
//
// The same defect as the reminder sweep, with a larger blast radius: EscalateAsync notifies the reviewer, the
// submitter AND every tenant administrator, so one overlapped sweep duplicated the escalation for the whole
// admin group rather than for one person. The sweep read candidates whose EscalatedAt was null, sent the
// notifications, stamped the markers and saved once at the end — correct transactionally (ADR 0794), with
// nothing excluding a second sweep that had read the same null.
//
// And the overlap is the ARRANGEMENT: ADR 0808 runs two app instances, each registering
// WorkflowEscalationWorker on its own timer.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ConcurrentEscalationSweepTests
{
    private readonly E2EApiFactory _factory;

    public ConcurrentEscalationSweepTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Two_sweeps_at_once_escalate_an_overdue_review_exactly_once()
    {
        var review = await OverdueReviewAsync();

        var acted = await Task.WhenAll(_factory.RunEscalationSweepAsync(), _factory.RunEscalationSweepAsync());

        // The COUNT, per recipient. A `Contains` would pass on two, which is the defect.
        Assert.Equal(1, await OverdueCountAsync(review));

        // And exactly one sweep claimed it — the mechanism. Without this the notification count could be 1 by
        // luck of ordering and the next change would break it again unnoticed.
        Assert.Equal(1, acted.Count(a => a >= 1));
    }

    [Fact]
    public async Task A_single_sweep_still_escalates()
    {
        // The anti-vacuous half: an exclusion that excluded everything would pass the test above.
        var review = await OverdueReviewAsync();

        Assert.True(await _factory.RunEscalationSweepAsync() >= 1);
        Assert.Equal(1, await OverdueCountAsync(review));
    }

    private sealed record Review(HttpClient Reviewer, Guid DocumentId);

    private async Task<int> OverdueCountAsync(Review review) =>
        (await TestJson.Get(review.Reviewer, "/api/notifications")).GetProperty("notifications").EnumerateArray()
            .Count(n => n.GetProperty("type").GetString() == "ReviewOverdue"
                        && n.GetProperty("documentId").GetGuid() == review.DocumentId);

    /// <summary>A submitted review that is already past its deadline, and the reviewer's client.</summary>
    private async Task<Review> OverdueReviewAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"esc-reviewer-{Guid.NewGuid():N}@e2e.local";
        const string password = "review1234";
        var reviewerId = await _factory.SeedUserAsync(tenantId, email, password, "Escalation Reviewer");

        // A 0-day SLA means the review is due the moment it is submitted, so the first sweep escalates.
        var maskId = await _factory.SeedMaskWithSlaAsync(tenantId, reviewSlaDays: 0);

        var repoId = (await TestJson.Post(owner, "/api/repositories",
            new { name = $"Esc {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{reviewerId}",
            new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        var documentId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = $"esc-doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{documentId}/mask", new { maskId })).EnsureSuccessStatusCode();

        var created = await TestJson.Post(owner, $"/api/documents/{documentId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{documentId}/versions/{versionId}", new { });
        await TestJson.Post(owner, $"/api/documents/{documentId}/versions/{versionId}/workflow/submit", new { reviewerId });

        return new Review(
            _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password)), documentId);
    }
}
