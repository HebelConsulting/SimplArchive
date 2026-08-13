using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API (in-process) + real Postgres + MinIO + OpenSearch + Tika, exercising the
// OpenSearch full-text search path (ADR "OpenSearch full-text slice 1", 0253; indexed ACL 0256; async
// indexing 0255). Indexing is asynchronous (outbox → SearchIndexWorker, ~3s cadence) and OpenSearch is
// eventually consistent, so the assertions poll. One comprehensive test — the container/upload setup is
// expensive — covering: content full-text, repository scoping, indexed-ACL filtering, and delete-removal.
[Collection(E2ECollection.Name)]
public class SearchTests
{
    private readonly E2EApiFactory _factory;

    public SearchTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Full_text_search_finds_by_content_and_respects_scope_acl_and_delete()
    {
        // A distinctive word placed ONLY in the document content (never the name), unique per run so reruns
        // against the shared database don't collide — a hit can only come from content extraction (Tika).
        var word = $"zzyzx{Guid.NewGuid():N}";

        var (ownerClientId, ownerSecret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(ownerClientId, ownerSecret));

        // A second principal in the SAME tenant with no ACL grant on anything — proves indexed-ACL filtering.
        var (outsiderClientId, outsiderSecret) = await _factory.SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: false);
        using var outsider = _factory.CreateAuthedClient(await _factory.GetTokenAsync(outsiderClientId, outsiderSecret));

        var (repoA, docA) = await CreateRepoWithDocAsync(owner, "search-a", "alpha-doc", $"annual figures {word} confidential");
        var (_, docB) = await CreateRepoWithDocAsync(owner, "search-b", "beta-doc", $"unrelated notes {word} here");

        // 1) Content full-text — the word is content-only, so finding both docs proves Tika extraction + OpenSearch
        //    indexing. Poll until BOTH are indexed: docA and docB are independent async index operations, so docA
        //    being indexed doesn't imply docB is yet (asserting docB immediately was the source of a flake).
        await PollAsync(
            async () => (await SearchIdsAsync(owner, word)) is var ids && ids.Contains(docA) && ids.Contains(docB),
            "docA and docB indexed by their content");

        // 2) Repository scoping — narrowing to repo A returns docA and excludes docB.
        var scoped = await SearchIdsAsync(owner, word, repoA);
        Assert.Contains(docA, scoped);
        Assert.DoesNotContain(docB, scoped);

        // 3) Indexed-ACL filtering — the outsider (same tenant, no CanSee) sees neither document. Both are
        //    confirmed indexed by now (step 1), so their allowedPrincipals are in place; a single check suffices.
        var outsiderHits = await SearchIdsAsync(outsider, word);
        Assert.DoesNotContain(docA, outsiderHits);
        Assert.DoesNotContain(docB, outsiderHits);

        // 4) Delete removes from the index — soft-delete docA, then it drops out of search results.
        await DeleteDocumentAsync(owner, docA);
        await PollAsync(async () => !(await SearchIdsAsync(owner, word, repoA)).Contains(docA), "docA removed from the index after delete");
    }

    [Fact]
    public async Task Search_finds_a_document_by_its_annotation_text()
    {
        // A distinctive token placed ONLY in an annotation's text (never the name/content), unique per run.
        var word = $"annofind{Guid.NewGuid():N}";

        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var (_, docId) = await CreateRepoWithDocAsync(owner, "search-anno", "anno-doc", "nothing notable here");
        var versionId = (await TestJson.Get(owner, $"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray().First().GetProperty("id").GetGuid();

        // A note whose text carries the token → the document becomes findable by it (ADR 0526).
        var anno = await TestJson.Post(owner, $"/api/documents/{docId}/versions/{versionId}/annotations",
            new { pageIndex = 0, kind = 0, positionX = 0.1, positionY = 0.1, width = 0.2, height = 0.06, text = $"stamp says {word}", color = "#FFEB3B" });
        var annoId = anno.GetProperty("id").GetGuid();

        await PollAsync(async () => (await SearchIdsAsync(owner, word)).Contains(docId), "document indexed by its annotation text");

        // Removing the annotation drops the token from the index → the document is no longer found by it.
        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{docId}/versions/{versionId}/annotations/{annoId}");
        del.Headers.TryAddWithoutValidation("If-Match", $"\"{anno.GetProperty("etag").GetString()}\"");
        (await owner.SendAsync(del)).EnsureSuccessStatusCode();

        await PollAsync(async () => !(await SearchIdsAsync(owner, word)).Contains(docId), "document dropped from the index after the annotation is removed");
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<(Guid RepoId, Guid DocId)> CreateRepoWithDocAsync(HttpClient owner, string repoPrefix, string docName, string content)
    {
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"{repoPrefix}-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = docName })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });
        return (repoId, docId);
    }

    // A hit hands out the addresses a client needs to act on it (#462): `versions` is what a preview follows to
    // reach the current version's `preview` and `text-layout` rels. Without it the Search tab would have to
    // compose `api/documents/{id}/versions`, which is the thing ADR 0543 exists to stop.
    //
    // Asserted per KIND, not just presence: a folder has nothing to preview, and advertising `versions` on one
    // would have the client offer a preview affordance the server cannot honour.
    [Fact]
    public async Task A_search_hit_advertises_the_rels_a_client_acts_on()
    {
        var word = $"zzyzx{Guid.NewGuid():N}";
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // The repository itself is a folder (a document with no parent), and its name carries the word — so one
        // search returns both kinds and the two branches are compared against the same response.
        var (repoId, docId) = await CreateRepoWithDocAsync(owner, $"rels-{word}", "hit-doc", $"content {word} here");

        await PollAsync(
            async () => (await SearchIdsAsync(owner, word)).Contains(docId),
            "the document is indexed");

        var response = await TestJson.Get(owner, $"/api/search?q={Uri.EscapeDataString(word)}");
        var hits = response.GetProperty("results").EnumerateArray().ToList();

        var doc = hits.Single(h => h.GetProperty("id").GetGuid() == docId);
        var docRels = doc.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()).ToHashSet();
        Assert.Contains("self", docRels);
        Assert.Contains("versions", docRels);

        // The `versions` address must actually resolve — a rel that 404s is worse than no rel, because the
        // client offers the affordance and then fails at the point of use.
        var versionsHref = doc.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "versions").GetProperty("href").GetString()!;
        var versions = await TestJson.Get(owner, versionsHref);
        Assert.NotEmpty(versions.GetProperty("versions").EnumerateArray().ToList());

        if (hits.FirstOrDefault(h => h.GetProperty("id").GetGuid() == repoId) is { ValueKind: System.Text.Json.JsonValueKind.Object } folder)
        {
            var folderRels = folder.GetProperty("links").EnumerateArray()
                .Select(l => l.GetProperty("rel").GetString()).ToHashSet();
            Assert.Contains("self", folderRels);
            Assert.DoesNotContain("versions", folderRels);
        }
    }

    private static async Task<HashSet<Guid>> SearchIdsAsync(HttpClient client, string q, Guid? repositoryId = null)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(q)}";
        if (repositoryId is { } r)
        {
            url += $"&repositoryId={r}";
        }

        var response = await TestJson.Get(client, url);
        return response.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
    }

    private static async Task DeleteDocumentAsync(HttpClient client, Guid documentId)
    {
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{documentId}"));
        head.EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{documentId}");
        request.Headers.TryAddWithoutValidation("If-Match", head.Headers.ETag!.ToString());
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    // Polls an eventually-consistent condition (async indexing + OpenSearch refresh) until it holds or times out.
    private static async Task PollAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {timeoutSeconds}s waiting for: {what}");
    }
}
