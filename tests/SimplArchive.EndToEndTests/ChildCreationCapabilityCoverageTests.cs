using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// `canCreateChildren` (#854, formerly the `create-child` rel — #634, ADR 0637) is what both clients gate New
// folder, Upload and the drop-zone on, so
// a surface that omits it does not merely lose a flag — it HIDES a working action, silently, on whatever nodes
// that surface feeds. And a surface that offers it where the others withhold it is the same failure inverted.
//
// It became a FLAG in #854 because the rel addressed the same URL as `children` and differed only by method:
// one address under two names (ADR 0719). The conversion also closed a gap the rel had — three of its four
// emission sites tested only the mask rule and not the caller's rights, because a per-row rights resolution
// used to cost a query per row. So this now asserts BOTH halves.
//
// `ChildCreationPolicyAgreementTests` asks whether the predicate agrees with the invariant. It cannot ask this:
// whether every surface a client reads actually emits the rel, and emits it consistently. Neither gap is
// hypothetical — both were live in the same week:
//
//   - the rel shipped on the children listing and on GET /documents/{id} but NOT on GET /repositories, which
//     hid "New subfolder" on every shared repository root in both clients while the create worked fine;
//   - the children ENVELOPE advertised it unconditionally, so the same folder answered yes through its own
//     collection and no through its parent's listing.
//
// So this walks all four surfaces and asserts each answers in the direction it should.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ChildCreationRelCoverageTests
{
    private readonly E2EApiFactory _factory;

    public ChildCreationRelCoverageTests(E2EApiFactory factory) => _factory = factory;

    private static bool CanCreateChildren(System.Text.Json.JsonElement resource) =>
        resource.TryGetProperty("canCreateChildren", out var flag)
        && flag.ValueKind == System.Text.Json.JsonValueKind.True;

    [Fact]
    public async Task Every_surface_a_tree_node_comes_from_answers_the_create_capability()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var serviceAccount = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repositoryName = $"rel-coverage-{Guid.NewGuid():N}"[..24];
        var repositoryId = (await TestJson.Post(serviceAccount, "/api/repositories", new { name = repositoryName }))
            .GetProperty("id").GetGuid();

        var email = $"relcov-{Guid.NewGuid():N}@e2e.local";
        const string password = "relcover1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Rel Coverage", isTenantAdmin: true);
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // 1. The REPOSITORIES listing — where both clients' top-level tree nodes come from. A repository root
        //    takes subfolders, so its row must say so; nothing re-fetches a tree node to populate its menu.
        var repositories = await TestJson.Get(user, "/api/repositories");
        var row = repositories.GetProperty("repositories").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == repositoryId);
        Assert.True(CanCreateChildren(row),
            "GET /api/repositories answered canCreateChildren=false, so both clients hide \"New subfolder\" on every "
            + "shared repository root — an action the server accepts.");

        // POSTing to the `children` address creates the folder, which is the half a flag alone cannot prove —
        // and it is the same address `children` serves for GET, which is the whole point of #854.
        var createHref = row.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "children").GetProperty("href").GetString()!;
        var childName = $"child-{Guid.NewGuid():N}"[..16];
        var childId = (await TestJson.Post(user, createHref, new { name = childName })).GetProperty("id").GetGuid();

        // 2. The CHILDREN listing — where every deeper tree node comes from. A plain folder takes subfolders too.
        var children = await TestJson.Get(user, $"/api/documents/{repositoryId}/children");
        var childRow = children.GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == childId);
        Assert.True(CanCreateChildren(childRow),
            "The children listing answered canCreateChildren=false on a plain folder, which hides \"New subfolder\" "
            + "everywhere below a repository root.");

        // 3. The PERSONAL root — the one surface that must WITHHOLD it, since its first level holds only the
        //    folders it was provisioned with (#634, ADR 0636). Asserted in the negative on purpose: a rel that
        //    is true everywhere proves nothing about a flag meaning "not available to you, here, now".
        var personal = await TestJson.Post(user, "/api/me/personal-repository", new { });
        Assert.False(CanCreateChildren(personal),
            "The personal repository answered canCreateChildren=true, so both clients would offer a create the first level "
            + "refuses (ADR 0636).");

        // …while My Documents, inside it, does offer it — otherwise the user has nowhere to put anything.
        var personalId = personal.GetProperty("id").GetGuid();
        var personalChildren = await TestJson.Get(user, $"/api/documents/{personalId}/children");
        var myDocuments = personalChildren.GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Documents");
        Assert.True(CanCreateChildren(myDocuments),
            "My Documents answered canCreateChildren=false, which leaves the user with no place to create a folder "
            + "in their personal space at all.");

        // 4. The children ENVELOPE — the collection's own links, as opposed to a row's. It advertised
        //    the create unconditionally, which is the row-level bug one level up: a caller that reached
        //    `Personal` through its own collection rather than through a parent listing got the OPPOSITE answer
        //    to the same question. One create, one rel, one answer (ADR 0637).
        Assert.False(CanCreateChildren(personalChildren),
            "The children envelope for the personal root answered canCreateChildren=true, contradicting what the "
            + "same folder answers everywhere else.");

        var repositoryChildren = await TestJson.Get(user, $"/api/documents/{repositoryId}/children");
        Assert.True(CanCreateChildren(repositoryChildren),
            "The children envelope for an ordinary repository answered canCreateChildren=false, so a caller holding "
            + "the collection believes it cannot create in it.");
    }

    // The half the rel could not answer (#854). `create-child` was emitted on three of its four sites from the
    // MASK rule alone — the comment at its listing site said why: a per-row rights resolution was "a query per
    // row on the hottest path there is". So a reader who could see a folder was told it could create in it, and
    // learned otherwise from a 403. GetCallerRightsForManyAsync removed that cost; this asserts the flag now
    // means what a client reads it to mean.
    [Fact]
    public async Task A_reader_is_told_it_cannot_create_even_where_the_mask_admits_one()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var serviceAccount = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repositoryId = (await TestJson.Post(serviceAccount, "/api/repositories", new { name = $"cap-{Guid.NewGuid():N}"[..24] }))
            .GetProperty("id").GetGuid();

        var ownerEmail = $"capowner-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, ownerEmail, "owner-1234", "Cap Owner", isTenantAdmin: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(ownerEmail, "owner-1234"));

        var readerEmail = $"capreader-{Guid.NewGuid():N}@e2e.local";
        var readerId = await _factory.SeedUserAsync(tenantId, readerEmail, "read-1234", "Cap Reader");
        (await owner.PutAsJsonAsync($"/api/documents/{repositoryId}/acl-entries/users/{readerId}",
            new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(readerEmail, "read-1234"));

        // A repository root's mask admits a plain child, so the MASK half is true here for everyone — which is
        // exactly what makes this a test of the rights half rather than of the policy.
        var asOwner = await TestJson.Get(owner, $"/api/documents/{repositoryId}");
        Assert.True(CanCreateChildren(asOwner), "The mask half must be true here, or this test proves nothing.");

        foreach (var (surface, resource) in new[]
        {
            ("GET /api/documents/{id}", await TestJson.Get(reader, $"/api/documents/{repositoryId}")),
            ("the children envelope", await TestJson.Get(reader, $"/api/documents/{repositoryId}/children")),
            ("GET /api/repositories", (await TestJson.Get(reader, "/api/repositories")).GetProperty("repositories")
                .EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == repositoryId)),
        })
        {
            Assert.False(CanCreateChildren(resource),
                $"{surface} answered canCreateChildren=true for a caller holding only CanSee/CanReadContent, so "
                + "both clients would offer Upload and New folder and the server would refuse them (ADR 0543).");
        }
    }
}
