namespace SimplArchive.EndToEndTests;

// The `folders` rel (#634) is what both clients gate "New folder" / "New subfolder" on, so a surface that omits
// it does not merely lose a link — it HIDES a working action, silently, on whatever nodes that surface feeds.
//
// `FolderCreationPolicyAgreementTests` asks whether the predicate agrees with the invariant. It cannot ask this:
// whether every listing a client builds a tree node from actually emits the rel. That gap is not hypothetical —
// the rel shipped on the children listing and on GET /documents/{id} but not on GET /repositories, which left
// "New subfolder" hidden on every shared repository root in both clients while the create worked fine.
//
// So this walks the three surfaces a tree node can come from and asserts each one answers, in the direction it
// should: the repositories listing and the children listing offer it, the personal root withholds it.
[Collection(E2ECollection.Name)]
public class FolderCreationRelCoverageTests
{
    private readonly E2EApiFactory _factory;

    public FolderCreationRelCoverageTests(E2EApiFactory factory) => _factory = factory;

    private static bool HasFolders(System.Text.Json.JsonElement resource) =>
        resource.TryGetProperty("links", out var links)
        && links.EnumerateArray().Any(l => l.GetProperty("rel").GetString() == "folders");

    [Fact]
    public async Task Every_surface_a_tree_node_comes_from_answers_the_folders_rel()
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
        Assert.True(HasFolders(row),
            "GET /api/repositories omitted the `folders` rel, so both clients hide \"New subfolder\" on every "
            + "shared repository root — an action the server accepts.");

        // Following it creates the folder, which is the half a link-presence assertion cannot prove.
        var createHref = row.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "folders").GetProperty("href").GetString()!;
        var childName = $"child-{Guid.NewGuid():N}"[..16];
        var childId = (await TestJson.Post(user, createHref, new { name = childName })).GetProperty("id").GetGuid();

        // 2. The CHILDREN listing — where every deeper tree node comes from. A plain folder takes subfolders too.
        var children = await TestJson.Get(user, $"/api/documents/{repositoryId}/children");
        var childRow = children.GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == childId);
        Assert.True(HasFolders(childRow),
            "The children listing omitted the `folders` rel on a plain folder, which hides \"New subfolder\" "
            + "everywhere below a repository root.");

        // 3. The PERSONAL root — the one surface that must WITHHOLD it, since its first level holds only the
        //    folders it was provisioned with (#634, ADR 0636). Asserted in the negative on purpose: a rel that
        //    is present everywhere proves nothing about a rel meaning "not available to you, here, now".
        var personal = await TestJson.Post(user, "/api/me/personal-repository", new { });
        Assert.False(HasFolders(personal),
            "The personal repository advertised `folders`, so both clients would offer a create the first level "
            + "refuses (ADR 0636).");

        // …while My Documents, inside it, does offer it — otherwise the user has nowhere to put anything.
        var personalId = personal.GetProperty("id").GetGuid();
        var myDocuments = (await TestJson.Get(user, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Documents");
        Assert.True(HasFolders(myDocuments),
            "My Documents withheld the `folders` rel, which leaves the user with no place to create a folder "
            + "in their personal space at all.");
    }
}
