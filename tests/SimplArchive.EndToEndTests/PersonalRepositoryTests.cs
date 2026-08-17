using System.Net;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the per-user personal repository (ADR "Per-user personal
// repository"): POST /api/me/personal-repository is get-or-create (idempotent), the repository is excluded from
// the shared GET /repositories list, a ServiceAccount has none (403), and — the key isolation guarantee — two
// users each get their own private space and neither can see the other's personal documents.
[Collection(E2ECollection.Name)]
public class PersonalRepositoryTests
{
    private readonly E2EApiFactory _factory;

    public PersonalRepositoryTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Personal_repository_is_idempotent_excluded_from_the_shared_list_and_denied_to_service_accounts()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);

        var email = $"personal-{Guid.NewGuid():N}@e2e.local";
        const string password = "personal1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Personal User");
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Get-or-create: the first POST creates it, a second returns the exact same repository.
        var first = await TestJson.Post(user, "/api/me/personal-repository", new { });
        var personalId = first.GetProperty("id").GetGuid();
        Assert.Equal("Personal", first.GetProperty("name").GetString());
        var second = await TestJson.Post(user, "/api/me/personal-repository", new { });
        Assert.Equal(personalId, second.GetProperty("id").GetGuid());

        // The personal repository never appears in the shared repository list.
        var shared = await TestJson.Get(user, "/api/repositories");
        Assert.DoesNotContain(shared.GetProperty("repositories").EnumerateArray(), r => r.GetProperty("id").GetGuid() == personalId);

        // A ServiceAccount has no personal space.
        using var serviceAccount = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        Assert.Equal(HttpStatusCode.Forbidden, (await serviceAccount.PostAsync("/api/me/personal-repository", null)).StatusCode);
    }

    [Fact]
    public async Task Personal_repository_is_seeded_with_a_My_Documents_subfolder_idempotently()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var email = $"mydocs-{Guid.NewGuid():N}@e2e.local";
        const string password = "personal1234";
        await _factory.SeedUserAsync(tenantId, email, password, "MyDocs User");
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalId = (await TestJson.Post(user, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();

        // The personal repository is seeded with "My Documents" plus the typed folders: "Notebook" (#562
        // slice 5, renamed from "Notes" by #564) and "My Calendar"/"My Contacts" (#564); the Intray /
        // Check-out launchers are a client-side tree concept, not API children. The order is the list
        // endpoint's (CreatedAt, Id), i.e. the order the provisioner creates them in — not alphabetical.
        //
        // "Notebook" here and "Notes" over IMAP is deliberate, not drift: one folder with two projections,
        // and the wire name is what a notes client looks for (see ImapEndpointTests).
        Assert.Equal(["My Documents", "Notebook", "My Calendar", "My Contacts"], await ChildNamesAsync(user, personalId));

        // A second ensure does not duplicate either — the idempotent backfill.
        await TestJson.Post(user, "/api/me/personal-repository", new { });
        Assert.Equal(["My Documents", "Notebook", "My Calendar", "My Contacts"], await ChildNamesAsync(user, personalId));
    }

    [Fact]
    public async Task Two_users_private_documents_are_isolated_from_each_other()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var emailA = $"alice-{Guid.NewGuid():N}@e2e.local";
        var emailB = $"bob-{Guid.NewGuid():N}@e2e.local";
        const string password = "personal1234";
        await _factory.SeedUserAsync(tenantId, emailA, password, "Alice");
        await _factory.SeedUserAsync(tenantId, emailB, password, "Bob");

        using var alice = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(emailA, password));
        using var bob = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(emailB, password));

        // Each user ensures their own personal repository and files a private document into it.
        var aliceRepo = (await TestJson.Post(alice, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var bobRepo = (await TestJson.Post(bob, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        Assert.NotEqual(aliceRepo, bobRepo);

        var aliceDoc = await UploadDocumentAsync(alice, aliceRepo, "alice-secret");
        var bobDoc = await UploadDocumentAsync(bob, bobRepo, "bob-secret");

        // Each sees only their own document under their own repository.
        Assert.Contains(await ChildIdsAsync(alice, aliceRepo), id => id == aliceDoc);
        Assert.Contains(await ChildIdsAsync(bob, bobRepo), id => id == bobDoc);

        // Neither can reach the other's personal repository or its document.
        AssertDenied((await bob.GetAsync($"/api/documents/{aliceRepo}")).StatusCode);
        AssertDenied((await bob.GetAsync($"/api/documents/{aliceRepo}/children")).StatusCode);
        AssertDenied((await bob.GetAsync($"/api/documents/{aliceDoc}")).StatusCode);
        AssertDenied((await alice.GetAsync($"/api/documents/{bobRepo}")).StatusCode);
        AssertDenied((await alice.GetAsync($"/api/documents/{bobDoc}")).StatusCode);

        // Neither personal repository is visible in the other user's shared repository list either.
        var bobShared = await TestJson.Get(bob, "/api/repositories");
        Assert.DoesNotContain(bobShared.GetProperty("repositories").EnumerateArray(), r => r.GetProperty("id").GetGuid() == aliceRepo);
    }

    private static async Task<Guid> UploadDocumentAsync(HttpClient client, Guid folderId, string name)
    {
        var docId = (await TestJson.Post(client, $"/api/documents/{folderId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(name)))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
        return docId;
    }

    private static async Task<List<Guid>> ChildIdsAsync(HttpClient client, Guid folderId)
    {
        var children = await TestJson.Get(client, $"/api/documents/{folderId}/children");
        return children.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
    }

    private static async Task<List<string>> ChildNamesAsync(HttpClient client, Guid folderId)
    {
        var children = await TestJson.Get(client, $"/api/documents/{folderId}/children");
        return children.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();
    }

    private static void AssertDenied(HttpStatusCode status) =>
        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"expected the request to be denied, got {status}");
}
