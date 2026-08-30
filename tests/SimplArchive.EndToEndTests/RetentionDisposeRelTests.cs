using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The `dispose` rel must be offered EXACTLY where the dispose endpoint accepts the call (#871).
//
// It used to be gated on the tenant-wide review policy alone, so it appeared on rows that are not yet due and on
// rows frozen by a legal hold. Both clients trusted it — as ADR 0543 says a client should — and offered Dispose
// on documents the server refuses (#870).
//
// Every case here asserts BOTH halves: that the rel is absent, and that the endpoint really would have refused.
// A test that checked only the absence would pass just as happily against a resource that lies, and a client
// that trusts absence deserves better evidence than that.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class RetentionDisposeRelTests
{
    private readonly E2EApiFactory _factory;

    public RetentionDisposeRelTests(E2EApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> RowAsync(HttpClient records, Guid documentId) =>
        (await TestJson.Get(records, "/api/retention/schedule")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("documentId").GetGuid() == documentId);

    private static bool HasDispose(JsonElement row) =>
        row.GetProperty("links").EnumerateArray().Any(l => l.GetProperty("rel").GetString() == "dispose");

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .TryGetProperty("errorCode", out var code) ? code.GetString() : null;

    private static async Task<Guid> MaskedChildAsync(HttpClient owner, Guid repoId, Guid maskId, string name)
    {
        var id = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{id}/mask", new { maskId })).EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task Dispose_is_advertised_only_on_rows_the_endpoint_would_accept()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"recrel-{Guid.NewGuid():N}@e2e.local";
        const string password = "recrel-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Records");
        await _factory.GrantCanManageClassificationAsync(email);
        await _factory.GrantCanLegalHoldAsync(email);
        using var records = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Ret {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // 0 years → due today. 5 years → not due, which is the case the rel used to be offered on anyway.
        var dueMask = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 0);
        var futureMask = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 5);

        var due = await MaskedChildAsync(owner, repoId, dueMask, $"due-{Guid.NewGuid():N}"[..12]);
        var notDue = await MaskedChildAsync(owner, repoId, futureMask, $"future-{Guid.NewGuid():N}"[..12]);
        var held = await MaskedChildAsync(owner, repoId, dueMask, $"held-{Guid.NewGuid():N}"[..12]);

        var holdId = (await TestJson.Post(records, "/api/legal-holds", new { name = $"Matter {Guid.NewGuid():N}"[..14] })).GetProperty("id").GetGuid();
        (await records.PostAsJsonAsync($"/api/legal-holds/{holdId}/items", new { documentId = held })).EnsureSuccessStatusCode();

        // 1. Due, unheld, no review policy → offered, and it really works.
        var dueRow = await RowAsync(records, due);
        Assert.True(dueRow.GetProperty("overdue").GetBoolean());
        Assert.True(HasDispose(dueRow), "a due, unheld row must advertise dispose — otherwise the action is unreachable.");

        // 2. NOT yet due → withheld, and the endpoint refuses. This is the case #871 was filed for: the rel was
        //    emitted here, so a client could offer Dispose on a document years from its disposition date.
        var notDueRow = await RowAsync(records, notDue);
        Assert.False(notDueRow.GetProperty("overdue").GetBoolean());
        Assert.False(HasDispose(notDueRow), "a row that is not yet due must not advertise dispose.");

        var notDueRefusal = await records.PostAsync($"/api/retention/{notDue}/dispose", null);
        Assert.Equal(HttpStatusCode.BadRequest, notDueRefusal.StatusCode);
        Assert.Equal("DOCUMENT_NOT_ELIGIBLE_FOR_DISPOSITION", await ErrorCodeAsync(notDueRefusal));

        // 3. Frozen by a legal hold → withheld, and the endpoint refuses. Compliance overrides disposition, so
        //    offering it here promised the user something the archive is legally obliged to refuse.
        var heldRow = await RowAsync(records, held);
        Assert.True(heldRow.GetProperty("suspendedByHold").GetBoolean());
        Assert.False(HasDispose(heldRow), "a legal-held row must not advertise dispose.");

        var heldRefusal = await records.PostAsync($"/api/retention/{held}/dispose", null);
        Assert.Equal(HttpStatusCode.Conflict, heldRefusal.StatusCode);
        Assert.Equal("LEGAL_HOLD", await ErrorCodeAsync(heldRefusal));

        // `extend` stays available throughout — it is what a not-yet-due or held document is FOR, and withdrawing
        // it alongside dispose would have been an easy over-correction.
        Assert.All(
            new[] { dueRow, notDueRow, heldRow },
            row => Assert.Contains(row.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "extend"));
    }

    [Fact]
    public async Task Turning_on_disposition_review_withdraws_dispose_from_a_row_that_had_it()
    {
        // The tenant-wide half, asserted as a TRANSITION on one unchanged document — so the difference can only
        // be the policy. This is the condition both clients omitted from their re-derived gate (#870): with
        // review on, the row is still overdue and still unheld, and the answer is still no.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"recrev-{Guid.NewGuid():N}@e2e.local";
        const string password = "recrev-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Records");
        await _factory.GrantCanManageClassificationAsync(email);
        await _factory.GrantTenantAdminAsync(email);
        using var records = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Rev {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var maskId = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 0);
        var doc = await MaskedChildAsync(owner, repoId, maskId, $"rev-{Guid.NewGuid():N}"[..12]);

        var before = await RowAsync(records, doc);
        Assert.True(HasDispose(before));

        await TestJson.Put(records, "/api/tenant-settings/records",
            new { auditRetentionDays = 90, wormLockMode = 0, requireDispositionReview = true });

        var after = await RowAsync(records, doc);
        Assert.True(after.GetProperty("overdue").GetBoolean(), "still overdue — only the policy changed.");
        Assert.False(after.GetProperty("suspendedByHold").GetBoolean(), "still unheld — only the policy changed.");
        Assert.False(HasDispose(after), "review-before-disposition must withdraw the dispose rel.");
    }
}
