using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising retention review-before-disposition (ADR "Retention
// review-before-disposition"): a records manager manually Disposes an eligible document, Extends another (which
// then isn't disposable), can't dispose a legal-held one, and a non-manager is refused.
[Collection(E2ECollection.Name)]
public class RetentionDispositionReviewTests
{
    private readonly E2EApiFactory _factory;

    public RetentionDispositionReviewTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Dispose_extend_legal_hold_and_authorization()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"records-{Guid.NewGuid():N}@e2e.local";
        const string password = "records-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Records");
        await _factory.GrantCanManageClassificationAsync(email);
        await _factory.GrantCanLegalHoldAsync(email);
        using var records = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Three documents assigned a 0-year-retention mask → all immediately eligible.
        var maskId = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 0);
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Ret {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var toDispose = await MaskedChildAsync(owner, repoId, maskId, "dispose-me");
        var toExtend = await MaskedChildAsync(owner, repoId, maskId, "extend-me");
        var held = await MaskedChildAsync(owner, repoId, maskId, "held");

        // Manual dispose → the document is soft-deleted (recycle bin, so 404 now).
        Assert.Equal(HttpStatusCode.NoContent, (await records.PostAsync($"/api/retention/{toDispose}/dispose", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/documents/{toDispose}")).StatusCode);

        // Extend → the schedule shows the override + no longer overdue, and disposing it is now refused.
        var until = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(3).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.NoContent, (await records.PostAsJsonAsync($"/api/retention/{toExtend}/extend", new { until })).StatusCode);
        var row = (await TestJson.Get(records, "/api/retention/schedule")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("documentId").GetGuid() == toExtend);
        Assert.Equal(until, row.GetProperty("retentionOverrideUntil").GetString());
        Assert.False(row.GetProperty("overdue").GetBoolean());
        var reDispose = await records.PostAsync($"/api/retention/{toExtend}/dispose", null);
        Assert.Equal(HttpStatusCode.BadRequest, reDispose.StatusCode);
        Assert.Equal("DOCUMENT_NOT_ELIGIBLE_FOR_DISPOSITION", await ErrorCodeAsync(reDispose));

        // A past-dated extension is rejected.
        var badExtend = await records.PostAsJsonAsync($"/api/retention/{held}/extend", new { until = "2000-01-01" });
        Assert.Equal(HttpStatusCode.BadRequest, badExtend.StatusCode);
        Assert.Equal("INVALID_RETENTION_EXTENSION", await ErrorCodeAsync(badExtend));

        // A legal hold blocks disposition.
        var holdId = (await TestJson.Post(records, "/api/legal-holds", new { name = "Matter" })).GetProperty("id").GetGuid();
        (await records.PostAsJsonAsync($"/api/legal-holds/{holdId}/items", new { documentId = held })).EnsureSuccessStatusCode();
        var heldDispose = await records.PostAsync($"/api/retention/{held}/dispose", null);
        Assert.Equal(HttpStatusCode.Conflict, heldDispose.StatusCode);
        Assert.Equal("LEGAL_HOLD", await ErrorCodeAsync(heldDispose));

        // A user without CanManageClassification can't dispose.
        var plainEmail = $"plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, plainEmail, password, "Plain");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(plainEmail, password));
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"/api/retention/{held}/dispose", null)).StatusCode);
    }

    private static async Task<Guid> MaskedChildAsync(HttpClient owner, Guid repoId, Guid maskId, string name)
    {
        var id = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{id}/mask", new { maskId })).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
    }
}
