using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of document tags (ADR "Document tags"): the real SimplArchiveApiClient sets a document's
// tags (normalized/deduped by the server), reads them back, lists the tenant tag catalog, and rejects a
// non-editor.
[Collection(UiCollection.Name)]
public class DesktopDocumentTagsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDocumentTagsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Set_read_and_catalog_tags()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"tags-{Guid.NewGuid():N}.txt";
        await api.Documents.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("tagged"));
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));

        Assert.Empty(await api.Documents.GetTagsAsync((await api.Documents.GetDocumentDetailAsync(doc.Id)).Href("tags"))); // none by default

        var unique = $"dt{Guid.NewGuid():N}"[..10];
        // Mixed case + a duplicate + blank → normalized (trimmed lowercase), deduped, sorted.
        var stored = await api.Documents.SetTagsAsync((await api.Documents.GetDocumentDetailAsync(doc.Id)).Href("tags"), [$"  {unique.ToUpperInvariant()} ", "Contract", unique, ""]);
        Assert.Equal(new[] { "contract", unique }, stored);
        Assert.Equal(new[] { "contract", unique }, await api.Documents.GetTagsAsync((await api.Documents.GetDocumentDetailAsync(doc.Id)).Href("tags")));

        var catalog = await api.Documents.GetTagCatalogAsync();
        Assert.Contains(unique, catalog);
        Assert.Contains("contract", catalog);
    }
}
