using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// List-pane drop filing (ADR "List-pane drop filing"): dropping OS files onto a document row files them as a
// new version of that document (with an optional feed comment), and onto a folder / the pane files them as new
// documents (also with an optional comment). This covers the new api-client capability the drop paths use.
[Collection(UiCollection.Name)]
public class DesktopListPaneDropFilingTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopListPaneDropFilingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Filing_as_version_adds_a_version_and_posts_the_comment()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"dropver-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("v1"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));

        await api.UploadNewVersionAsync(doc.Id, Encoding.UTF8.GetBytes("v2 dropped"), ".txt", "dropped as a new version");

        var fields = await api.GetSystemFieldsAsync(doc.Id);
        Assert.Equal(2, fields!.CurrentVersionNumber);

        var comments = await api.GetCommentsAsync(doc.Id);
        Assert.Contains(comments, c => c.Body == "dropped as a new version");
    }

    [Fact]
    public async Task Filing_into_a_folder_creates_a_document_and_posts_the_comment()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var folderName = $"dropfolder-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == folderName);

        var docName = $"dropped-{Guid.NewGuid():N}.txt";
        var newId = await api.UploadFileAsync(folder.Id, docName, Encoding.UTF8.GetBytes("body"), "filed via drop");

        Assert.Contains(await api.GetChildrenAsync(folder.Id), n => n.Id == newId);
        var comments = await api.GetCommentsAsync(newId);
        Assert.Contains(comments, c => c.Body == "filed via drop");
    }
}
