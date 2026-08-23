using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// Index health is observable (#661): GET /api/search/reindex says whether the alias is MISSING (while it is,
// every search answers empty — the degradation ADR 0626 forbids leaving invisible) and how many outbox rows
// are pending. Before this the resource carried only a status string and a count from the last rebuild, so
// "search is permanently empty" and "search is fine" read identically to an administrator.
[Collection(E2ECollection.Name)]
public class SearchReindexHealthTests
{
    private readonly E2EApiFactory _factory;

    public SearchReindexHealthTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_reindex_status_carries_alias_and_outbox_health()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var status = await TestJson.Get(admin, "/api/search/reindex");

        // The fixture boots with OpenSearch configured and the startup backfill complete, so the alias
        // exists — false, not null (null means "not configured / unreachable", a different answer).
        Assert.Equal(JsonValueKind.False, status.GetProperty("aliasMissing").ValueKind);

        // The fixture also waits for the outbox to drain before handing the app over (#660), so pending is a
        // real number and normally zero. Asserted non-negative rather than zero: a concurrently-running test
        // in this collection may legitimately have rows in flight.
        Assert.True(status.GetProperty("pendingOutbox").GetInt32() >= 0);
        Assert.False(string.IsNullOrEmpty(status.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_tenant_principal_is_refused_the_platform_surface()
    {
        // The index spans every tenant, so this is platform maintenance — a tenant's own admin has no
        // business reading (or triggering) it.
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var tenant = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await tenant.GetAsync("/api/search/reindex")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await tenant.PostAsync("/api/search/reindex", null)).StatusCode);
    }
}
