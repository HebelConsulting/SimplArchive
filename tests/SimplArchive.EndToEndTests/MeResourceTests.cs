namespace SimplArchive.EndToEndTests;

// The caller's own account resource (#416 created it; #464 gave it the email).
//
// It had no direct coverage — only its sub-resources did — which is how a resource whose entire job is to be the
// entry point for a client's self-service goes unverified. Every rel here is one a client follows instead of
// composing a URL (ADR 0543), so a rel silently disappearing would break both clients quietly.
[Collection(E2ECollection.Name)]
public class MeResourceTests
{
    private readonly E2EApiFactory _factory;

    public MeResourceTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task It_carries_the_callers_own_email_and_the_rels_a_client_follows()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"me-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Me");
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var me = await TestJson.Get(user, "/api/me");

        // The email is what a profile screen shows to say which account you are signed in as (#464), and it must
        // be the CALLER's — a resource that returned someone else's would be a disclosure, not a display bug.
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.NotEqual(Guid.Empty, me.GetProperty("userId").GetGuid());

        // The rels both clients follow for self-service. Asserted by NAME because a rename is invisible at
        // runtime — the client simply stops offering the affordance (ADR 0543).
        var rels = me.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()).ToHashSet();
        foreach (var rel in new[] { "self", "changePassword", "photo", "mfa", "passkeys", "webdavPassword", "personalRepository", "notificationPreferences" })
        {
            Assert.Contains(rel, rels);
        }
    }

    [Fact]
    public async Task A_principal_with_no_personal_account_resolves_with_no_email_and_no_rels()
    {
        // A service account has no person behind it. The resource still RESOLVES rather than 404ing, so a client
        // can ask without special-casing its principal type, and the absent rels mean "not available to you"
        // exactly as everywhere else (ADR 0543).
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        using var machine = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var me = await TestJson.Get(machine, "/api/me");

        Assert.True(me.GetProperty("email").ValueKind is System.Text.Json.JsonValueKind.Null);
        var rels = me.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()).ToList();
        Assert.Equal(["self"], rels);
    }
}
