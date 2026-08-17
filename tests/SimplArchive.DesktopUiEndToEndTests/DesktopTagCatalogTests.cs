using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the tag catalog (ADR "Tag controlled vocabulary"): the real SimplArchiveApiClient
// creates a coloured catalog tag, renames it (documents are re-tagged), merges it into another, and retires it.
[Collection(UiCollection.Name)]
public class DesktopTagCatalogTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTagCatalogTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Create_rename_merge_and_retire()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var alpha = $"alpha{suffix}";
        var beta = $"beta{suffix}";

        // A document tagged with 'alpha' (populates the catalog + gives rename/merge something to re-tag).
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var docName = $"tagcat-{suffix}.txt";
        await api.Documents.UploadFileAsync(repo.Href("children"), docName, Encoding.UTF8.GetBytes("body"));
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(docName));
        await api.Documents.SetTagsAsync((await api.Documents.GetDocumentDetailAsync(doc.Href("self"))).Href("tags"), [alpha]);

        // Create a second, coloured catalog tag directly.
        await api.Documents.CreateTagAsync(beta, "#2e7d32");

        var catalog = await api.Documents.GetTagCatalogWithColorsAsync();
        Assert.True(catalog.CanManage);
        var alphaDef = catalog.Items.Single(t => t.Name == alpha);
        Assert.Equal("#2e7d32", catalog.Items.Single(t => t.Name == beta).Color);

        // Rename 'alpha' → 'gamma…' cascades to the document.
        var gamma = $"gamma{suffix}";
        await api.Documents.UpdateTagAsync(alphaDef, gamma, null);
        Assert.Equal(new[] { gamma }, await api.Documents.GetTagsAsync((await api.Documents.GetDocumentDetailAsync(doc.Href("self"))).Href("tags")));

        // Merge 'gamma' into 'beta' → the document now carries 'beta', and 'gamma' is gone from the catalog.
        var gammaDef = (await api.Documents.GetTagCatalogWithColorsAsync()).Items.Single(t => t.Name == gamma);
        var betaDef = (await api.Documents.GetTagCatalogWithColorsAsync()).Items.Single(t => t.Name == beta);
        await api.Documents.MergeTagAsync(gammaDef, betaDef.Id);
        Assert.Equal(new[] { beta }, await api.Documents.GetTagsAsync((await api.Documents.GetDocumentDetailAsync(doc.Href("self"))).Href("tags")));
        Assert.DoesNotContain((await api.Documents.GetTagCatalogWithColorsAsync()).Items, t => t.Name == gamma);

        // Retire 'beta' → excluded from the active catalog.
        await api.Documents.RetireTagAsync(betaDef);
        Assert.DoesNotContain((await api.Documents.GetTagCatalogWithColorsAsync()).Items, t => t.Name == beta);
    }
}
