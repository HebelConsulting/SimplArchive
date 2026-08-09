using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of list-row columns (ADR "List-row columns and sorting"): the child listing the real
// SimplArchiveApiClient returns carries the document type (assigned mask), the version's size + document
// date, and the tags — the data the sortable columns bind to.
[Collection(UiCollection.Name)]
public class DesktopListColumnsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopListColumnsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Child_nodes_carry_type_size_date_and_tags()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var folderName = $"cols-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == folderName);

        var docName = $"coldoc-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(folder.Id, docName, Encoding.UTF8.GetBytes(new string('y', 2048)));
        var doc = (await api.GetChildrenAsync(folder.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(docName));
        await api.SetTagsAsync((await api.GetDocumentDetailAsync(doc.Id)).Href("tags"), ["Red"]);

        var node = (await api.GetChildrenAsync(folder.Href("children"))).Single(n => n.Id == doc.Id);
        Assert.Equal("Basic Entry", node.DocumentType);   // auto-classified mask name
        Assert.Equal(2048, node.SizeBytes);
        Assert.NotNull(node.DocumentDate);
        Assert.Equal(["red"], node.Tags);
    }
}
