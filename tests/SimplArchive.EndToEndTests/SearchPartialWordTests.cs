using System.Text;

namespace SimplArchive.EndToEndTests;

// The partial-word fallback (ADR "Partial-word search fallback"). The index is analyzed with the standard
// analyzer, so every field holds WHOLE WORDS — searching "montage" against a document containing
// "Montagehalterung" returned nothing at all, which reads as a broken search rather than a strict one. When the
// whole-word pass finds nothing, the query is retried once with each term wrapped in wildcards.
//
// Both halves matter and are asserted together, because the second is what stops the first from being a
// regression: the fallback must fire when the precise search found NOTHING, and must not fire when it found
// something (or a search for a real word would start dragging in every longer word containing it).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SearchPartialWordTests
{
    private readonly E2EApiFactory _factory;

    public SearchPartialWordTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_fragment_finds_the_compound_word_but_a_whole_word_match_still_wins()
    {
        // One run-unique stem so reruns against the shared database can't collide, used to build two documents:
        //   compound  — the stem GLUED to more letters, so the indexed token is stem+"halterung" and nothing else
        //   whole     — the stem standing alone, so the indexed token is exactly the stem
        // Alphanumeric throughout: the standard analyzer splits on non-alphanumerics, so a hyphen would make the
        // "compound" two tokens and the test would pass without the fallback existing at all.
        var stem = $"zzq{Guid.NewGuid():N}";

        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"partial-{stem}" })).GetProperty("id").GetGuid();
        var compound = await CreateDocAsync(owner, repoId, "compound-doc", $"technische Zeichnung {stem}halterung aus Stahl");
        var whole = await CreateDocAsync(owner, repoId, "whole-doc", $"Lieferschein {stem} Position 4");

        // Both are indexed once each is findable by a term that needs NO fallback — the compound by its full
        // token, the standalone by itself. Polling on the fragment instead would make an indexing delay
        // indistinguishable from the fallback not working.
        await PollAsync(async () => (await SearchIdsAsync(owner, $"{stem}halterung")).Contains(compound), "the compound document is indexed");
        await PollAsync(async () => (await SearchIdsAsync(owner, stem)).Contains(whole), "the standalone document is indexed");

        // 1) The bug. "montage" against "Montagehalterung": a fragment that is a whole word NOWHERE, so the
        //    precise pass returns nothing and the fallback is the only thing that can answer.
        var fragmentHits = await SearchIdsAsync(owner, $"{stem}halt");
        Assert.Contains(compound, fragmentHits);

        // 2) The guard. The stem IS a whole word in the standalone document, so the precise pass finds it and
        //    the fallback must never run — otherwise every exact search would also return the longer words that
        //    merely contain it, and precision would be gone for everyone to fix the minority case.
        var wholeWordHits = await SearchIdsAsync(owner, stem);
        Assert.Contains(whole, wholeWordHits);
        Assert.DoesNotContain(compound, wholeWordHits);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static async Task<Guid> CreateDocAsync(HttpClient owner, Guid parentId, string name, string content)
    {
        var docId = (await TestJson.Post(owner, $"/api/documents/{parentId}/children", new { name = $"{name}-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });
        return docId;
    }

    private static async Task<HashSet<Guid>> SearchIdsAsync(HttpClient client, string q)
    {
        var response = await TestJson.Get(client, $"/api/search?q={Uri.EscapeDataString(q)}");
        return response.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
    }

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
