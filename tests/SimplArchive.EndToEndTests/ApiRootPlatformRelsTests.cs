using System.Linq;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The API root advertises the platform-administrator surface to a platform administrator, and to nobody else
// (#1409, ADR 0543). Four collections — provisioning a tenant, managing platform administrators, KEK rotation
// and the search reindex — were reachable by NO rel, so a conforming client had to conclude they did not exist;
// `saconsole` was composing `api/tenants` for exactly that reason.
//
// Both halves are asserted here because either alone is the wrong shape. Emitting them to everyone would hand a
// signed-in user four addresses that can only ever answer 403 — the lying affordance ADR 0543 exists to prevent —
// and emitting them to nobody is the gap this closes. Note the asymmetry with the rest of the root, which is
// deliberate: a tenant-scoped rel is advertised to every caller because the followed resource answers what they
// may do, while a platform administrator is a different PRINCIPAL KIND with no tenant at all (ADR 0206).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ApiRootPlatformRelsTests
{
    private static readonly string[] PlatformRels =
        ["tenants", "platformAdministrators", "encryptionKeys", "searchReindex"];

    private readonly E2EApiFactory _factory;

    public ApiRootPlatformRelsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_platform_administrator_is_offered_the_platform_collections()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var rels = RelsOf(await TestJson.Get(admin, "/api"));

        Assert.All(PlatformRels, rel => Assert.Contains(rel, rels));
    }

    [Fact]
    public async Task A_tenant_principal_and_an_anonymous_caller_are_offered_none_of_them()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var tenantPrincipal = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        using var anonymous = _factory.CreateClient();

        var withTenant = RelsOf(await TestJson.Get(tenantPrincipal, "/api"));
        var withoutToken = RelsOf(await TestJson.Get(anonymous, "/api"));

        Assert.All(PlatformRels, rel => Assert.DoesNotContain(rel, withTenant));
        Assert.All(PlatformRels, rel => Assert.DoesNotContain(rel, withoutToken));

        // …and the root is still the ordinary discovery document for both — a test that merely found four rels
        // missing would also pass against a root that had stopped answering at all.
        Assert.Contains("repositories", withTenant);
        Assert.Contains("theme", withoutToken);
    }

    [Fact]
    public async Task The_advertised_tenants_href_is_the_one_that_provisions_a_tenant()
    {
        // The rel is only worth anything if following it reaches the endpoint. Asserted by FOLLOWING rather than
        // by comparing to a literal path: a test that pinned "/api/tenants" would pass while the route moved,
        // which is the compatibility surface ADR 0543 deliberately does NOT put in the path.
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var href = await TestJson.Get(admin, "/api") is var root
            ? root.GetProperty("links").EnumerateArray()
                .First(l => l.GetProperty("rel").GetString() == "tenants")
                .GetProperty("href").GetString()!
            : throw new InvalidOperationException();

        var listed = await TestJson.Get(admin, href);

        Assert.True(listed.TryGetProperty("tenants", out var tenants));
        Assert.Equal(JsonValueKind.Array, tenants.ValueKind);
    }

    private static IReadOnlyList<string> RelsOf(JsonElement resource) =>
        resource.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString() ?? string.Empty)
            .ToList();
}
