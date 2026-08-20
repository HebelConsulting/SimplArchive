using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of search facets (ADR "Search facets"): the real DesktopClient SimplArchiveApiClient's
// SearchWithFacetsAsync returns document-type / year facet counts, and a document-type drill-down narrows the
// results. Uses two of the demo tenant's existing masks + a unique content word so only these docs match.
[Collection(UiCollection.Name)]
public class DesktopSearchFacetsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSearchFacetsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Facets_are_returned_and_document_type_drills_down()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var word = $"dtfacet{suffix}";

        // Two masks genuinely assignable to a plain document. This used to pick "Folder" as the second one,
        // which was a FOLDER mask stamped on a filed document — the very state #580 exists to prevent, and it
        // stopped being offered once the server started saying so (ADR 0653). A tenant-authored mask is what a
        // real user would reach for: no folder flag, no extension, no containment, so freely assignable.
        var maskA = (await api.Masks.GetMasksAsync()).First(m => m.Name == "Basic Entry");
        var maskB = await api.Masks.CreateAsync($"Facet Type {suffix}");

        await api.Documents.CreateRepositoryAsync($"dt-facets-{suffix}");
        var repo = (await api.Documents.GetRepositoriesAsync()).First(r => r.Name == $"dt-facets-{suffix}");
        var a1 = await UploadClassifiedAsync(api, repo, $"a1-{suffix}", word, maskA.Id);
        var a2 = await UploadClassifiedAsync(api, repo, $"a2-{suffix}", word, maskA.Id);
        var b1 = await UploadClassifiedAsync(api, repo, $"b1-{suffix}", word, maskB.Id);

        // Wait until all three are content-indexed AND their mask assignments have re-indexed. SetMaskAsync
        // re-indexes asynchronously and separately from the content, so the document-type facet counts settle a
        // beat after the docs become searchable — otherwise b1 is transiently still counted under its upload-time
        // default mask, inflating maskA's count (a real flake: expected 2, actual 3).
        await PollAsync(async () =>
        {
            var facets = await api.Search.SearchWithFacetsAsync($"q={word}");
            var ids = facets.Results.Select(r => r.Id).ToHashSet();
            if (!(ids.Contains(a1) && ids.Contains(a2) && ids.Contains(b1)))
            {
                return false;
            }

            var aCount = facets.Facets.DocumentTypes.Where(f => f.Value == maskA.Name).Select(f => f.Count).FirstOrDefault();
            var bCount = facets.Facets.DocumentTypes.Where(f => f.Value == maskB.Name).Select(f => f.Count).FirstOrDefault();
            return aCount == 2 && bCount == 1;
        });

        var page = await api.Search.SearchWithFacetsAsync($"q={word}");
        Assert.Equal(2, page.Facets.DocumentTypes.Single(f => f.Value == maskA.Name).Count);
        Assert.Equal(1, page.Facets.DocumentTypes.Single(f => f.Value == maskB.Name).Count);
        Assert.NotEmpty(page.Facets.Years);

        // Drill down by document type → only the two maskA documents.
        var drilled = (await api.Search.SearchWithFacetsAsync($"q={word}&system[documentType][eq]={Uri.EscapeDataString(maskA.Name)}")).Results.Select(r => r.Id).ToHashSet();
        Assert.Contains(a1, drilled);
        Assert.Contains(a2, drilled);
        Assert.DoesNotContain(b1, drilled);

        // File-type facet (ADR "Search facet refinements") — all three are .txt.
        Assert.Equal(3, page.Facets.FileTypes.Single(f => f.Value == "txt").Count);

        // Post-filter faceting: after selecting maskA, its OWN dimension stays open (maskB still shows).
        var afterA = await api.Search.SearchWithFacetsAsync($"q={word}&system[documentType][in]={Uri.EscapeDataString(maskA.Name)}");
        Assert.Contains(afterA.Facets.DocumentTypes, f => f.Value == maskB.Name);

        // Multi-select OR within a dimension → both types' documents.
        var both = (await api.Search.SearchWithFacetsAsync($"q={word}&system[documentType][in]={Uri.EscapeDataString(maskA.Name)},{Uri.EscapeDataString(maskB.Name)}")).Results.Select(r => r.Id).ToHashSet();
        Assert.Contains(a1, both);
        Assert.Contains(b1, both);
    }

    private static async Task<Guid> UploadClassifiedAsync(SimplArchiveApiClient api, Node repo, string name, string content, Guid maskId)
    {
        await api.Documents.UploadFileAsync(repo.Href("children"), $"{name}.txt", Encoding.UTF8.GetBytes(content));
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == name);
        await api.Masks.SetMaskAsync(doc.Href("mask"), maskId);
        return doc.Id;
    }

    private static async Task PollAsync(Func<Task<bool>> condition, int timeoutSeconds = 90)
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

        throw new Xunit.Sdk.XunitException($"Timed out after {timeoutSeconds}s waiting for indexing.");
    }
}
