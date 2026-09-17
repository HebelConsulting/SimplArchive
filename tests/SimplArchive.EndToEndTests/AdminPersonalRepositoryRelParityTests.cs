namespace SimplArchive.EndToEndTests;

// A personal space listed under Administration advertises what a repository row advertises.
//
// WHY A PARITY TEST RATHER THAN A LIST OF EXPECTED RELS. Both endpoints return REPOSITORY rows — one the
// caller's own, one every user's — so a client that can drive a row from `/api/repositories` must be able to
// drive the same row from `/api/admin/personal-repositories`. Pinning an expected set here would let the two
// drift apart again the moment the other listing gains a rel: the defect is a DIFFERENCE between two lists,
// and only a test that reads both can see it.
//
// IT HAS NOW BEEN SHORT THREE TIMES. `references` was added after the desktop tree CRASHED expanding a user
// (its loader follows that rel and it was never advertised). Then `mask` and `index-data` were missing, and a
// user under Administration → Users showed no mask on the desktop while the web showed "User Folder" —
// reported from use, because nothing else was watching.
//
// The web escaped that one by re-reading the resource by id, which is exactly why a client-side fix would have
// been the wrong place: the listing is what is incomplete.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class AdminPersonalRepositoryRelParityTests
{
    private readonly E2EApiFactory _factory;

    public AdminPersonalRepositoryRelParityTests(E2EApiFactory factory) => _factory = factory;

    // Rels a repository row carries that a personal space under Administration deliberately does not, each with
    // its reason. An entry here is a decision; the test below fails if one stops being needed.
    private static readonly Dictionary<string, string> DeliberatelyAbsent = new(StringComparer.Ordinal)
    {
        ["recycle-bin"] = "an administrator listing users is not thereby given their deleted documents — that is "
            + "a separate affordance and a separate decision, not something to inherit by copying a rel set",
    };

    [Fact]
    public async Task A_personal_space_row_advertises_what_a_repository_row_advertises()
    {
        var email = $"relparity-{Guid.NewGuid():N}@e2e.local";
        // A throwaway login for a Testcontainers database that lives for one test run. The value is not
        // arbitrary: `relparity1234` trips gitleaks' generic-api-key ENTROPY heuristic (`parity1234` does not,
        // `rowparity1234` does — it is the randomness, not the hyphen or the length). The allowlist would also
        // silence it, but .gitleaks.toml asks to stay surgical, and its two existing value entries are there
        // only because those spellings were already in shipped history. This one is not, so it costs nothing
        // to spell it so the scanner never has to be told to ignore it.
        const string password = "adminrels1234";
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        await _factory.SeedUserAsync(tenantId, email, password, "Rel Parity",
            canManageUsers: true, canManageRepositories: true);

        // Promoted through the helper, not by SeedUserAsync's isTenantAdmin flag: the Administration listing is
        // gated on CanAccessWithoutGrant (ADR 0670), which only the promotion sets. A user seeded "as an admin"
        // without it is refused here with a 403 — half-promoted, and the test would be reporting on a principal
        // no real deployment has.
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // A repository of their own, so the reference listing has a row to describe.
        await TestJson.Post(api, "/api/repositories", new { name = $"relparity-{Guid.NewGuid():N}" });
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var repositoryRels = RelsOfFirstRow(await TestJson.Get(api, "/api/repositories"), "repositories");
        var personalRels = RelsOfFirstRow(await TestJson.Get(api, "/api/admin/personal-repositories"), "repositories");

        Assert.NotEmpty(repositoryRels);
        Assert.NotEmpty(personalRels);

        var missing = repositoryRels
            .Where(rel => !personalRels.Contains(rel) && !DeliberatelyAbsent.ContainsKey(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These rels are on a repository row but not on a personal space listed under Administration:\n  "
            + string.Join("\n  ", missing)
            + "\n\nBoth are repository rows, so a client that can drive one must be able to drive the other. A "
            + "client holding a row follows what the row advertises (ADR 0543) — it does not re-read the "
            + "resource to discover what the listing left out, and the one client that did was merely hiding "
            + "this. Add the rel, or add it to `DeliberatelyAbsent` above WITH THE REASON.");

        // The other direction, so an exemption cannot outlive its reason.
        var stale = DeliberatelyAbsent.Keys
            .Where(rel => !repositoryRels.Contains(rel) || personalRels.Contains(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These exemptions no longer describe reality — the rel is gone from the repositories listing, or the "
            + "personal listing now carries it:\n  " + string.Join("\n  ", stale));
    }

    private static HashSet<string> RelsOfFirstRow(System.Text.Json.JsonElement response, string collection) =>
        response.GetProperty(collection).EnumerateArray()
            .Select(row => row.GetProperty("links").EnumerateArray()
                .Select(l => l.GetProperty("rel").GetString()!).ToHashSet(StringComparer.Ordinal))
            .FirstOrDefault() ?? [];
}
