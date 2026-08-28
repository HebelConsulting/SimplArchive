using System.Net;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage: after the audit events are sealed into WORM segments,
// GET /api/audit-events/worm-verify reads the immutable segments back and confirms they match the DB (ADR "Audit
// WORM segment verify"). A caller without CanViewAuditLog is refused.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class AuditWormVerifyTests
{
    private readonly E2EApiFactory _factory;

    public AuditWormVerifyTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Worm_verify_confirms_sealed_segments_match_the_db()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"worm-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "worm-1234", "Worm Auditor", canViewAuditLog: true);

        // Logging in records an Auth.LoggedIn audit event, so the tenant's chain is non-empty.
        using var auditor = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "worm-1234"));

        // Seal the pending events into WORM segments, then verify the segments against the DB.
        await _factory.RunWormArchiveAsync(tenantId);

        var result = await TestJson.Get(auditor, "/api/audit-events/worm-verify");
        Assert.True(result.GetProperty("valid").GetBoolean());
        Assert.True(result.GetProperty("segmentCount").GetInt32() >= 1);
        Assert.True(result.GetProperty("checkedCount").GetInt32() >= 1);

        // A caller without CanViewAuditLog (the ServiceAccount) is refused.
        using var sa = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        Assert.Equal(HttpStatusCode.Forbidden, (await sa.GetAsync("/api/audit-events/worm-verify")).StatusCode);
    }
}
