using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API (in-process) + real Postgres, exercising the audit trail (ADR "Audit trail
// (first slice)"): a security-sensitive action is recorded at its mutation site, then read back through the
// gated GET /api/audit-events endpoint. Covers that a ServiceAccount actor's action is recorded with its name
// snapshot, that a User holding CanViewAuditLog can read it (own ∪ groups via the resolver), that a User
// without the right is forbidden, and that the action filter narrows the result.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class AuditTrailTests
{
    private readonly E2EApiFactory _factory;

    public AuditTrailTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Auditable_action_is_recorded_and_readable_by_a_viewer_but_gated_from_others()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // Perform an auditable action: create a document, then delete it (records Document.Deleted).
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Audit {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"audit-doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // Delete needs If-Match (the document's ETag).
        var head = await owner.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{docId}"));
        var etag = head.Headers.ETag!.ToString();
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{docId}");
        delete.Headers.TryAddWithoutValidation("If-Match", etag);
        (await owner.SendAsync(delete)).EnsureSuccessStatusCode();

        // A User with CanViewAuditLog can read the log and see the recorded event.
        var viewerEmail = $"auditor-{Guid.NewGuid():N}@e2e.local";
        const string password = "audit1234";
        await _factory.SeedUserAsync(tenantId, viewerEmail, password, "Auditor", canViewAuditLog: true);
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, password));

        var events = (await TestJson.Get(viewer, "/api/audit-events")).GetProperty("events");
        var deleted = events.EnumerateArray().FirstOrDefault(e =>
            e.GetProperty("action").GetString() == "Document.Deleted" &&
            e.GetProperty("targetId").GetGuid() == docId);
        Assert.Equal(JsonValueKind.Object, deleted.ValueKind);
        Assert.Equal("ServiceAccount", deleted.GetProperty("actorType").GetString());
        Assert.False(string.IsNullOrEmpty(deleted.GetProperty("actorName").GetString()));
        Assert.Equal("Document", deleted.GetProperty("targetType").GetString());

        // The action filter narrows to exactly that action.
        var filtered = (await TestJson.Get(viewer, "/api/audit-events?action=Document.Deleted")).GetProperty("events");
        Assert.All(filtered.EnumerateArray(), e => Assert.Equal("Document.Deleted", e.GetProperty("action").GetString()));
        Assert.Contains(filtered.EnumerateArray(), e => e.GetProperty("targetId").GetGuid() == docId);

        // The tamper-evidence hash chain verifies clean over the real recorded events (ADR "Audit trail hash chain").
        var verify = await TestJson.Get(viewer, "/api/audit-events/verify");
        Assert.True(verify.GetProperty("valid").GetBoolean());
        Assert.True(verify.GetProperty("checkedCount").GetInt32() > 0);

        // NDJSON export (ADR "Audit trail export"): one JSON event per line, oldest-first, each carrying the
        // Sequence + Hash so the feed is independently verifiable.
        using var exportResponse = await viewer.GetAsync("/api/audit-events/export");
        exportResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/x-ndjson", exportResponse.Content.Headers.ContentType?.MediaType);
        var ndjson = await exportResponse.Content.ReadAsStringAsync();
        var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        var parsed = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        // Every line has the chain fields; the export is ordered by ascending Sequence.
        Assert.All(parsed, e => Assert.False(string.IsNullOrEmpty(e.GetProperty("hash").GetString())));
        var sequences = parsed.Select(e => e.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal(sequences.OrderBy(s => s).ToList(), sequences);
        Assert.Contains(parsed, e => e.GetProperty("action").GetString() == "Document.Deleted");
        // A non-viewer can't export.
        // (asserted below alongside the other endpoints)

        // Retention (ADR "Audit trail retention and purge"): a viewer reads the window (default 365, nothing
        // purged yet) but cannot change it or purge — those are tenant-admin governance actions.
        var retention = await TestJson.Get(viewer, "/api/audit-events/retention");
        Assert.Equal(365, retention.GetProperty("retentionDays").GetInt32());
        Assert.Equal(0, retention.GetProperty("chainStartSequence").GetInt64());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/audit-events/retention", new { retentionDays = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/audit-events/purge", null)).StatusCode);

        // A User without the right is forbidden — viewing the audit log is treated as sensitive.
        var outsiderEmail = $"outsider-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, outsiderEmail, password, "Outsider");
        using var outsider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(outsiderEmail, password));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/audit-events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/audit-events/verify")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/audit-events/retention")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/audit-events/export")).StatusCode);
    }
}
