using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// One rel per resource; the method says which action (ADR 0719, issue #694).
//
// Three resources advertised the same URL two or three times over — `self[GET]` beside `edit[PUT]` beside
// `revoke[DELETE]` — which multiplies the vocabulary a client must learn and the names a rename has to
// preserve, while adding nothing the HTTP method does not already carry.
//
// These tests pin the SHAPE, because that is the compatibility surface (ADR 0543): a client is entitled to
// find exactly the rels advertised here, and to reach the actions by method. They fail against the previous
// shape, which is the point of writing them.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class OneRelPerResourceTests
{
    private readonly E2EApiFactory _factory;

    public OneRelPerResourceTests(E2EApiFactory factory) => _factory = factory;

    private static List<string> RelsOf(JsonElement resource) =>
        [.. resource.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!)];

    private static string HrefOf(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel)
            .GetProperty("href").GetString()!;

    [Fact]
    public async Task The_imap_access_resource_advertises_its_address_once()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"onerel-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "onerel-1234", "One Rel");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "onerel-1234"));

        var status = await TestJson.Get(api, "/api/me/imap-access");

        // `settings` survives — it is a DIFFERENT address. `generate` and `revoke` were this one under two
        // more names, and what actually gated the buttons was `available`/`enabled`, which are still here.
        Assert.Equal(["self", "settings"], RelsOf(status).Order());
        Assert.True(status.TryGetProperty("available", out _));
        Assert.True(status.TryGetProperty("enabled", out _));

        // The method is the action: POST issues a password at the same address, DELETE revokes it.
        var generated = await TestJson.Post(api, HrefOf(status, "self"), new { });
        Assert.True(generated.GetProperty("enabled").GetBoolean());

        (await api.DeleteAsync(HrefOf(status, "self"))).EnsureSuccessStatusCode();
        Assert.False((await TestJson.Get(api, "/api/me/imap-access")).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task A_service_account_advertises_its_address_once_and_says_whether_it_may_be_changed()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var admin = $"sa-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, admin, "sa-admin-1234", "SA Admin", canManageServiceAccounts: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(admin, "sa-admin-1234"));

        var created = await TestJson.Post(api, "/api/service-accounts", new { name = $"sa-{Guid.NewGuid():N}"[..12] });
        var id = created.GetProperty("id").GetGuid();

        var live = await TestJson.Get(api, $"/api/service-accounts/{id}");
        Assert.Equal(["rotate-secret", "self"], RelsOf(live).Order());

        // The capability the removed rels were really carrying, now said as a capability (ADR 0719).
        Assert.True(live.GetProperty("canManage").GetBoolean());

        // PUT at that one address edits it.
        (await api.PutAsJsonAsync(HrefOf(live, "self"), new { name = "renamed-by-method" }))
            .EnsureSuccessStatusCode();
        Assert.Equal("renamed-by-method", (await TestJson.Get(api, $"/api/service-accounts/{id}")).GetProperty("name").GetString());

        // DELETE at the same address revokes it — and THEN the answer changes, which is the part a client
        // gates on. A revoked account keeps its address and loses the permission, rather than losing the
        // address and leaving the client to infer why.
        (await api.DeleteAsync(HrefOf(live, "self"))).EnsureSuccessStatusCode();

        var revoked = await TestJson.Get(api, $"/api/service-accounts/{id}");
        Assert.Equal(["self"], RelsOf(revoked));
        Assert.False(revoked.GetProperty("canManage").GetBoolean());
    }

    [Fact]
    public async Task An_acl_entry_advertises_its_address_once_and_the_principal_row_still_names_its_transition()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repo = (await TestJson.Post(api, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var email = $"grantee-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "grantee-1234", "Grantee");

        // A principal with no entry yet advertises `grant` — conditionally, so its presence names the
        // available transition. That is NOT a verb duplicate and is deliberately left alone (ADR 0719).
        var principals = await TestJson.Get(api, $"/api/documents/{repo}/acl-entries/grantable-principals");
        var principal = principals.GetProperty("principals").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == userId);
        Assert.Equal(["grant"], RelsOf(principal));

        (await api.PutAsJsonAsync(HrefOf(principal, "grant"), new { canSee = true, canReadContent = true }))
            .EnsureSuccessStatusCode();

        var entries = await TestJson.Get(api, $"/api/documents/{repo}/acl-entries");
        var entry = entries.GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("principalId").GetGuid() == userId);

        // The entry itself names its address once; PUT replaces it and DELETE removes it.
        Assert.Equal(["self"], RelsOf(entry));

        (await api.PutAsJsonAsync(HrefOf(entry, "self"), new { canSee = true, canReadContent = true, canMove = true }))
            .EnsureSuccessStatusCode();
        (await api.DeleteAsync(HrefOf(entry, "self"))).EnsureSuccessStatusCode();

        var after = await TestJson.Get(api, $"/api/documents/{repo}/acl-entries");
        Assert.DoesNotContain(
            after.GetProperty("entries").EnumerateArray(), e => e.GetProperty("principalId").GetGuid() == userId);
    }
}
