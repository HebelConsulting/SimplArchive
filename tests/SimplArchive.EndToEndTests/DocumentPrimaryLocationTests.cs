using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for the set-primary-location / promote-a-reference endpoint (ADR 0506) over the real API + Postgres:
// promoting a referenced folder re-homes the real document there and leaves a reference at the former home (and
// drops the now-redundant target-side reference), and the guards (repository root / unchanged / missing If-Match)
// reject as specified.
[Collection(E2ECollection.Name)]
public class DocumentPrimaryLocationTests
{
    private readonly E2EApiFactory _factory;

    public DocumentPrimaryLocationTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Promoting_a_reference_re_homes_the_document_and_leaves_a_reference_behind()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"PL {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderA = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "Folder A" })).GetProperty("id").GetGuid();
        var folderB = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "Folder B" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{folderA}/children", new { name = "the-doc" })).GetProperty("id").GetGuid();

        // A pre-existing reference to the doc in Folder B — promotion should drop it as redundant.
        await PostJson(api, $"/api/documents/{folderB}/references", new { targetId = docId });

        // Before: the real home is Folder A; Folder B merely references it.
        var before = await GetJson(api, $"/api/documents/{docId}/referencing-folders");
        Assert.Equal(folderA, before.GetProperty("primaryLocation").GetProperty("id").GetGuid());
        Assert.Contains(folderB, FolderIds(before));
        Assert.DoesNotContain(folderA, FolderIds(before));

        // Promote Folder B to be the primary location.
        var promote = await PutWithIfMatch(api, $"/api/documents/{docId}/primary-location", await ETagAsync(api, docId), new { folderId = folderB });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        // After: the real home is Folder B; Folder A is now a reference; Folder B's redundant reference is gone.
        var after = await GetJson(api, $"/api/documents/{docId}/referencing-folders");
        Assert.Equal(folderB, after.GetProperty("primaryLocation").GetProperty("id").GetGuid());
        Assert.Contains(folderA, FolderIds(after));
        Assert.DoesNotContain(folderB, FolderIds(after));
    }

    [Fact]
    public async Task Promote_rejects_repository_root_unchanged_and_missing_if_match()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"PL {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folder = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "F" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "doc" })).GetProperty("id").GetGuid();

        // A repository root has no primary location to change → 409.
        var root = await PutWithIfMatch(api, $"/api/documents/{repoId}/primary-location", await ETagAsync(api, repoId), new { folderId = folder });
        Assert.Equal(HttpStatusCode.Conflict, root.StatusCode);

        // The doc already lives in the repo root; promoting to the repo root is a no-op → 409.
        var unchanged = await PutWithIfMatch(api, $"/api/documents/{docId}/primary-location", await ETagAsync(api, docId), new { folderId = repoId });
        Assert.Equal(HttpStatusCode.Conflict, unchanged.StatusCode);

        // Missing If-Match → 428.
        var noEtag = await api.PutAsJsonAsync($"/api/documents/{docId}/primary-location", new { folderId = folder });
        Assert.Equal(HttpStatusCode.PreconditionRequired, noEtag.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static IEnumerable<Guid> FolderIds(JsonElement referencingFolders) =>
        referencingFolders.GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("id").GetGuid());

    private static async Task<string> ETagAsync(HttpClient api, Guid documentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{documentId}");
        var response = await api.SendAsync(request);
        return response.Headers.ETag!.Tag;
    }

    private static Task<HttpResponseMessage> PutWithIfMatch(HttpClient api, string url, string etag, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return api.SendAsync(request);
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body) => await ReadJson(await client.PostAsJsonAsync(url, body));

    private static async Task<JsonElement> GetJson(HttpClient client, string url) => await ReadJson(await client.GetAsync(url));

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }

        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
