using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// A tag rename and a tag merge each used to be TWO transactions (#1170): `RetagAsync` committed every
// re-pointed DocumentTag row — and enqueued the search reindex — before the caller committed the
// TagDefinition itself. A failure on that second commit left every affected document carrying a tag name whose
// definition did not exist, with the index already told the new name.
//
// Both are now one transaction with one commit, and the reindex is enqueued AFTER it.
//
// The refusal path is what makes this testable end to end: a rename onto an EXISTING name is rejected with a
// name conflict, and the rejection has to leave the documents exactly as they were. Under the old shape the
// refusal came from a check BEFORE the retag, so this asserts the property rather than reproducing the old
// crash — the atomicity is the contract, and a later refusal added after the retag must not be able to break it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TagRenameAtomicityTests
{
    private readonly E2EApiFactory _factory;

    public TagRenameAtomicityTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Owner, HttpClient Admin, Guid DocumentId)> WorldAsync(params string[] tags)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"tagatom-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "tagatom-1234", "Tag Atom Admin");
        await _factory.GrantTenantAdminAsync(email);
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "tagatom-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Atom-{Guid.NewGuid():N}"[..14] })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"atom-{Guid.NewGuid():N}"[..12] })).GetProperty("id").GetGuid();
        await TestJson.Put(owner, $"/api/documents/{docId}/tags", new { tags });

        return (owner, admin, docId);
    }

    private static async Task<List<string>> TagsOfAsync(HttpClient api, Guid documentId) =>
        [.. (await TestJson.Get(api, $"/api/documents/{documentId}/tags")).GetProperty("tags").EnumerateArray()
            .Select(t => t.GetString()!)];

    private static async Task<Guid> TagIdAsync(HttpClient admin, string name) =>
        (await TestJson.Get(admin, "/api/tags")).GetProperty("catalog").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    [Fact]
    public async Task A_refused_rename_leaves_the_documents_exactly_as_they_were()
    {
        // Two catalogue entries exist; renaming one ONTO the other is refused. The document must still carry
        // the original name — not a name whose definition does not exist, which is the state #1170 could leave.
        var (owner, admin, docId) = await WorldAsync("atomone", "atomtwo");
        using var _o = owner;
        using var _a = admin;

        var response = await admin.PutAsJsonAsync($"/api/tags/{await TagIdAsync(admin, "atomone")}", new { name = "atomtwo" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var tags = await TagsOfAsync(owner, docId);
        Assert.Contains("atomone", tags);
        Assert.Contains("atomtwo", tags);

        // …and the catalogue still describes both, so no document points at a definition that is gone.
        var catalogue = (await TestJson.Get(admin, "/api/tags")).GetProperty("catalog").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("atomone", catalogue);
        Assert.Contains("atomtwo", catalogue);
    }

    [Fact]
    public async Task A_rename_moves_the_documents_and_the_definition_together()
    {
        var (owner, admin, docId) = await WorldAsync("atomsrc");
        using var _o = owner;
        using var _a = admin;

        await TestJson.Put(admin, $"/api/tags/{await TagIdAsync(admin, "atomsrc")}", new { name = "atomdst" });

        // The document moved…
        var tags = await TagsOfAsync(owner, docId);
        Assert.Contains("atomdst", tags);
        Assert.DoesNotContain("atomsrc", tags);

        // …and the catalogue agrees. Under two transactions these two facts could disagree.
        var catalogue = (await TestJson.Get(admin, "/api/tags")).GetProperty("catalog").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("atomdst", catalogue);
        Assert.DoesNotContain("atomsrc", catalogue);
    }

    [Fact]
    public async Task A_merge_moves_the_documents_and_removes_the_source_together()
    {
        var (owner, admin, docId) = await WorldAsync("atomfrom", "atomkeep");
        using var _o = owner;
        using var _a = admin;

        var sourceId = await TagIdAsync(admin, "atomfrom");
        var intoId = await TagIdAsync(admin, "atomkeep");
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/tags/{sourceId}/merge", new { intoId })).StatusCode);

        var tags = await TagsOfAsync(owner, docId);
        Assert.Contains("atomkeep", tags);
        Assert.DoesNotContain("atomfrom", tags);

        // The source definition is gone — and was removed in the SAME transaction that moved the documents, so
        // it cannot survive with zero documents on it (the old shape's other half).
        var catalogue = (await TestJson.Get(admin, "/api/tags")).GetProperty("catalog").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("atomfrom", catalogue);
        Assert.Contains("atomkeep", catalogue);
    }
}
