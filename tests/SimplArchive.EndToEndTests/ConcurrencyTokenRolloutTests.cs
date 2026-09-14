using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// The entities #1083 brought under optimistic concurrency — Tenant, User, ServiceAccount, AclEntry,
// WorkflowState — now carry a token, emit it as an ETag and honour If-Match.
//
// This is STEP ONE of a staged rollout, and the tests pin both halves of that stage deliberately:
//   * a caller that sends a STALE token is refused 412 — the lost update this exists to stop;
//   * a caller that sends NO token still succeeds — because requiring it is a breaking change for every client
//     that does not yet send one, so the header becomes mandatory only after both clients learn to send it.
// The second assertion is the one that will CHANGE in step three, and it is written down so that change is a
// deliberate edit rather than a surprise.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ConcurrencyTokenRolloutTests
{
    private readonly E2EApiFactory _factory;

    public ConcurrencyTokenRolloutTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_service_account_read_carries_an_etag_and_a_stale_one_is_refused()
    {
        using var api = await AdminClientAsync();

        var created = await TestJson.Post(api, "/api/service-accounts", new { name = $"sa-{Guid.NewGuid():N}"[..12] });
        var id = created.GetProperty("id").GetGuid();

        // The read hands out the token…
        var read = await api.GetAsync($"/api/service-accounts/{id}");
        read.EnsureSuccessStatusCode();
        var etag = read.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag), "the service-account read emitted no ETag");

        object Body(string name) => new
        {
            name,
            canManageRepositories = false,
            canManageMasks = false,
            canManageServiceAccounts = false,
            canImport = false,
            canExport = false,
            canBlockResources = false,
        };

        // …and a write carrying it succeeds, which also moves the token on.
        using var fresh = new HttpRequestMessage(HttpMethod.Put, $"/api/service-accounts/{id}")
        {
            Content = JsonContent.Create(Body($"first-{Guid.NewGuid():N}"[..14])),
        };
        fresh.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(fresh)).StatusCode);

        // The SAME token a second time is now stale — this is the second admin saving a form loaded before the
        // first admin's write. Refused rather than silently clobbering, which is the whole point (#1083).
        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/service-accounts/{id}")
        {
            Content = JsonContent.Create(Body($"second-{Guid.NewGuid():N}"[..14])),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await api.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task A_write_without_if_match_is_still_accepted_in_this_stage()
    {
        // STEP ONE's contract, pinned so step three is a deliberate change: a client that sends no token keeps
        // working. Enforcing 428 now would break every client that has not yet been taught to send one.
        using var api = await AdminClientAsync();

        var created = await TestJson.Post(api, "/api/service-accounts", new { name = $"sa-{Guid.NewGuid():N}"[..12] });
        var id = created.GetProperty("id").GetGuid();

        var response = await api.PutAsJsonAsync($"/api/service-accounts/{id}", new
        {
            name = $"renamed-{Guid.NewGuid():N}"[..14],
            canManageRepositories = false,
            canManageMasks = false,
            canManageServiceAccounts = false,
            canImport = false,
            canExport = false,
            canBlockResources = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_token_is_reported_as_a_concurrency_conflict_not_a_name_conflict()
    {
        // The ordering trap this change had to get right: DbUpdateConcurrencyException DERIVES from
        // DbUpdateException, and this endpoint already caught the latter to translate a unique-index collision
        // into a name conflict. Caught in the wrong order, a stale token is reported as a name collision — an
        // error naming a cause that never happened, which is worse than no error at all.
        using var api = await AdminClientAsync();

        var created = await TestJson.Post(api, "/api/service-accounts", new { name = $"sa-{Guid.NewGuid():N}"[..12] });
        var id = created.GetProperty("id").GetGuid();

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/service-accounts/{id}")
        {
            Content = JsonContent.Create(new
            {
                name = $"n-{Guid.NewGuid():N}"[..12],
                canManageRepositories = false,
                canManageMasks = false,
                canManageServiceAccounts = false,
                canImport = false,
                canExport = false,
                canBlockResources = false,
            }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{Guid.NewGuid()}\"");

        var response = await api.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Contains("ETAG_MISMATCH", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // A tenant admin who may manage service accounts — the same shape OneRelPerResourceTests uses.
    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var admin = $"conc-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, admin, "conc-admin-1234", "Concurrency Admin", canManageServiceAccounts: true);
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(admin, "conc-admin-1234"));
    }

}
