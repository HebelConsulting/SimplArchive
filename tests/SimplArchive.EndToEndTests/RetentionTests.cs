using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising retention auto-disposition (ADR "Retention policies
// (auto-disposition)"): a document assigned a mask whose retention has elapsed shows on the schedule and is
// auto-soft-deleted by the sweep; a legal-held document is listed as suspended and spared; the schedule is
// gated on CanManageClassification.
[Collection(E2ECollection.Name)]
public class RetentionTests
{
    private readonly E2EApiFactory _factory;

    public RetentionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Expired_documents_are_disposed_but_a_held_one_is_suspended()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A user who can view the schedule, and one who can place a legal hold.
        var email = $"records-{Guid.NewGuid():N}@e2e.local";
        const string password = "records-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Records");
        await _factory.GrantCanManageClassificationAsync(email);
        await _factory.GrantCanLegalHoldAsync(email);
        using var records = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Two documents assigned a mask with a 0-year retention → both immediately eligible.
        var maskId = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 0);
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Ret {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var disposableId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "expired-doc" })).GetProperty("id").GetGuid();
        var heldId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "held-doc" })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{disposableId}/mask", new { maskId })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/documents/{heldId}/mask", new { maskId })).EnsureSuccessStatusCode();

        // Place a legal hold on one of them.
        var holdId = (await TestJson.Post(records, "/api/legal-holds", new { name = "Matter" })).GetProperty("id").GetGuid();
        (await records.PostAsJsonAsync($"/api/legal-holds/{holdId}/items", new { documentId = heldId })).EnsureSuccessStatusCode();

        // The schedule lists both — the held one marked suspended, the other overdue.
        var schedule = (await TestJson.Get(records, "/api/retention/schedule")).GetProperty("items").EnumerateArray().ToList();
        var disposableRow = schedule.Single(i => i.GetProperty("documentId").GetGuid() == disposableId);
        var heldRow = schedule.Single(i => i.GetProperty("documentId").GetGuid() == heldId);
        Assert.True(disposableRow.GetProperty("overdue").GetBoolean());
        Assert.False(disposableRow.GetProperty("suspendedByHold").GetBoolean());
        Assert.True(heldRow.GetProperty("suspendedByHold").GetBoolean());

        // The sweep disposes the expired, un-held document (→ recycle bin, so 404 now) but spares the held one.
        await _factory.RunRetentionSweepAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/documents/{disposableId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{heldId}")).StatusCode);
    }

    [Fact]
    public async Task Schedule_requires_CanManageClassification()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"plain-{Guid.NewGuid():N}@e2e.local";
        const string password = "plain-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Plain");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/retention/schedule")).StatusCode);
    }
}
