using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API, exercising the "My work" dashboard's cross-document list endpoints (ADR "My
// work dashboard"): GET /api/reminders returns the caller's overdue + due-soon reminders (not ones far out),
// GET /api/subscriptions returns the documents they follow; both are User-only (a ServiceAccount → 403).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class MyWorkDashboardTests
{
    private readonly E2EApiFactory _factory;

    public MyWorkDashboardTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Reminders_and_subscriptions_lists_back_the_dashboard()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"mywork-{Guid.NewGuid():N}@e2e.local";
        const string password = "mywork-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "My Work");
        await _factory.GrantTenantAdminAsync(email);
        using var me = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"MW {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "mw-doc" })).GetProperty("id").GetGuid();

        // A due-soon reminder (tomorrow) + one far out (8 months). Only the due-soon one is on the dashboard.
        var soon = (await TestJson.Post(me, $"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(1), note = "Soon", recurrence = 0 })).GetProperty("id").GetGuid();
        var far = (await TestJson.Post(me, $"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddMonths(8), recurrence = 0 })).GetProperty("id").GetGuid();

        var reminders = (await TestJson.Get(me, "/api/reminders")).GetProperty("reminders").EnumerateArray().ToList();
        Assert.Contains(reminders, r => r.GetProperty("id").GetGuid() == soon && r.GetProperty("parentId").GetGuid() == repoId);
        Assert.DoesNotContain(reminders, r => r.GetProperty("id").GetGuid() == far);

        // Follow the document → it appears in the subscriptions list.
        (await me.PutAsync($"/api/documents/{docId}/subscription", null)).EnsureSuccessStatusCode();
        var followed = (await TestJson.Get(me, "/api/subscriptions")).GetProperty("followed").EnumerateArray().ToList();
        Assert.Contains(followed, f => f.GetProperty("documentId").GetGuid() == docId && f.GetProperty("parentId").GetGuid() == repoId);

        // A ServiceAccount has neither.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/subscriptions")).StatusCode);
    }
}
