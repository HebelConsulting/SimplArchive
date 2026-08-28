namespace SimplArchive.EndToEndTests;

// The `create-child` rel (#634, ADR 0637) is what both clients gate New folder, Upload and the drop-zone on, so
// a surface that omits it does not merely lose a link — it HIDES a working action, silently, on whatever nodes
// that surface feeds. And a surface that offers it where the others withhold it is the same failure inverted.
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

    private static bool HasCreateChild(System.Text.Json.JsonElement resource) =>
        resource.TryGetProperty("links", out var links)
        && links.EnumerateArray().Any(l => l.GetProperty("rel").GetString() == "create-child");

    [Fact]
    public async Task Every_surface_a_tree_node_comes_from_answers_the_create_child_rel()
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
        Assert.True(HasCreateChild(row),
            "GET /api/repositories omitted the `create-child` rel, so both clients hide \"New subfolder\" on every "
            + "shared repository root — an action the server accepts.");

        // Following it creates the folder, which is the half a link-presence assertion cannot prove.
        var createHref = row.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "create-child").GetProperty("href").GetString()!;
        var childName = $"child-{Guid.NewGuid():N}"[..16];
        var childId = (await TestJson.Post(user, createHref, new { name = childName })).GetProperty("id").GetGuid();

        // 2. The CHILDREN listing — where every deeper tree node comes from. A plain folder takes subfolders too.
        var children = await TestJson.Get(user, $"/api/documents/{repositoryId}/children");
        var childRow = children.GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == childId);
        Assert.True(HasCreateChild(childRow),
            "The children listing omitted the `create-child` rel on a plain folder, which hides \"New subfolder\" "
            + "everywhere below a repository root.");

        // 3. The PERSONAL root — the one surface that must WITHHOLD it, since its first level holds only the
        //    folders it was provisioned with (#634, ADR 0636). Asserted in the negative on purpose: a rel that
        //    is present everywhere proves nothing about a rel meaning "not available to you, here, now".
        var personal = await TestJson.Post(user, "/api/me/personal-repository", new { });
        Assert.False(HasCreateChild(personal),
            "The personal repository advertised `create-child`, so both clients would offer a create the first level "
            + "refuses (ADR 0636).");

        // …while My Documents, inside it, does offer it — otherwise the user has nowhere to put anything.
        var personalId = personal.GetProperty("id").GetGuid();
        var personalChildren = await TestJson.Get(user, $"/api/documents/{personalId}/children");
        var myDocuments = personalChildren.GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Documents");
        Assert.True(HasCreateChild(myDocuments),
            "My Documents withheld the `create-child` rel, which leaves the user with no place to create a folder "
            + "in their personal space at all.");

        // 4. The children ENVELOPE — the collection's own links, as opposed to a row's. It advertised
        //    `create-child` unconditionally, which is the row-level bug one level up: a caller that reached
        //    `Personal` through its own collection rather than through a parent listing got the OPPOSITE answer
        //    to the same question. One create, one rel, one answer (ADR 0637).
        Assert.False(HasCreateChild(personalChildren),
            "The children envelope for the personal root advertised `create-child`, contradicting the rel the "
            + "same folder withholds everywhere else.");

        var repositoryChildren = await TestJson.Get(user, $"/api/documents/{repositoryId}/children");
        Assert.True(HasCreateChild(repositoryChildren),
            "The children envelope for an ordinary repository withheld `create-child`, so a caller holding the "
            + "collection has no advertised way to create in it.");
    }
}
