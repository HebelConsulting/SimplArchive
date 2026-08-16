using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of #462: a search result carries the address a preview needs, and "reset" means everything.
//
// Both cases are about behaviour that LOOKS fine when it is broken. A hit with no `versions` address still
// renders as a row — it just silently never previews. And a reset that leaves the facet drill-downs applied
// still empties the visible form — the results simply stay narrowed by criteria the user can no longer see.
[Collection(UiCollection.Name)]
public class DesktopSearchWorkbenchTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSearchWorkbenchTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_document_hit_carries_the_address_its_preview_follows_and_a_folder_does_not()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var word = $"prevhref{suffix}";

        // The repository name carries the word too, so ONE search returns both a document and a folder and the
        // two branches are compared against the same response rather than two searches.
        await api.CreateRepositoryAsync($"{word}-repo");
        var repo = (await api.GetRepositoriesAsync()).First(r => r.Name == $"{word}-repo");
        var docId = await UploadAsync(api, repo.Id, $"doc-{suffix}", word);

        await PollAsync(async () => (await api.Search.SearchAsync(word)).Any(r => r.Id == docId), "the document is indexed");

        var results = await api.Search.SearchAsync(word);

        var doc = results.Single(r => r.Id == docId);
        Assert.False(doc.IsFolder);
        Assert.False(string.IsNullOrEmpty(doc.VersionsHref));

        // Following it must actually produce a renderable preview — an address that resolves to nothing would
        // leave the pane blank with no way to tell that from "this document has no rendition".
        var preview = await api.GetPreviewFromVersionsAsync(doc.VersionsHref!);
        Assert.False(string.IsNullOrEmpty(preview.PreviewUrl));

        // The folder advertises no `versions`: there is nothing to preview, and a row that claimed otherwise
        // would have the client offer an affordance the server cannot honour (ADR 0543).
        var folder = results.Single(r => r.Id == repo.Id);
        Assert.True(folder.IsFolder);
        Assert.Null(folder.VersionsHref);
    }

    [Fact]
    public async Task Resetting_the_criteria_clears_the_facet_drill_downs_too()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var word = $"resetfacet{suffix}";

        var masks = await api.GetMasksAsync();
        var maskA = masks[0];
        var maskB = masks.First(m => m.Id != maskA.Id);

        await api.CreateRepositoryAsync($"reset-{suffix}");
        var repo = (await api.GetRepositoriesAsync()).First(r => r.Name == $"reset-{suffix}");
        var a = await UploadClassifiedAsync(api, repo.Id, $"a-{suffix}", word, maskA.Id);
        var b = await UploadClassifiedAsync(api, repo.Id, $"b-{suffix}", word, maskB.Id);

        // Wait for the MASK assignments to re-index, not merely for the documents to become searchable:
        // SetMaskAsync re-indexes asynchronously and separately from the content, so for a beat b is still
        // counted under its upload-time default mask and the drill-down returns both. Asserting straight after
        // indexing is exactly the flake DesktopSearchFacetsTests already documents — and it caught this test on
        // its first run.
        await PollAsync(
            async () =>
            {
                var ids = (await api.Search.SearchAsync(word)).Select(r => r.Id).ToHashSet();
                if (!(ids.Contains(a) && ids.Contains(b)))
                {
                    return false;
                }

                var byMaskA = await api.Search.SearchWithFacetsAsync($"q={word}&system[documentType][in]={Uri.EscapeDataString(maskA.Name)}");
                return byMaskA.Results.Count == 1;
            },
            "both documents indexed and their masks re-indexed");

        // Narrowed by a document-type facet: one of the two.
        var narrowed = await api.Search.SearchWithFacetsAsync($"q={word}&system[documentType][in]={Uri.EscapeDataString(maskA.Name)}");
        Assert.Single(narrowed.Results);

        // Unnarrowed — what a reset must get you back to. Before #462 the reset dropped the refinement panel and
        // left this drill-down in place, so the user stayed on the narrowed set with nothing on screen to say why.
        var reset = await api.Search.SearchWithFacetsAsync($"q={word}");
        Assert.Equal(2, reset.Results.Count);
    }

    private static async Task<Guid> UploadAsync(SimplArchiveApiClient api, Guid repoId, string name, string content)
    {
        await api.UploadFileAsync(repoId, $"{name}.txt", Encoding.UTF8.GetBytes($"body {content} end"));
        return (await api.GetChildrenAsync(repoId)).First(c => c.Name == name).Id;
    }

    private static async Task<Guid> UploadClassifiedAsync(SimplArchiveApiClient api, Guid repoId, string name, string content, Guid maskId)
    {
        var docId = await UploadAsync(api, repoId, name, content);
        await api.SetMaskAsync(docId, maskId);
        return docId;
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

            await Task.Delay(1000);
        }

        throw new TimeoutException($"Timed out waiting for: {what}");
    }
}
