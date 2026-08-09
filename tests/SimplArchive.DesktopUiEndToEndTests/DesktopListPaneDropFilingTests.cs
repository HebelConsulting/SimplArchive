using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// List-pane drop filing (ADR "List-pane drop filing"): dropping OS files onto a document row files them as a
// new version of that document, and onto a folder / the pane files them as new documents. The optional filing
// comment is the version's "why this revision" note (ADR 0528) — set on DocumentVersion.Comment, not a chat
// comment. This covers the new api-client capability the drop paths use.
[Collection(UiCollection.Name)]
public class DesktopListPaneDropFilingTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopListPaneDropFilingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Filing_as_version_adds_a_version_and_sets_the_version_comment()
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

        // The filing comment is the version's note (ADR 0528), not a chat comment.
        var versions = await api.GetVersionsAsync(doc.Href("versions"));
        Assert.Equal("dropped as a new version", versions.Single(v => v.VersionNumber == 2).Comment);
    }

    [Fact]
    public async Task Filing_into_a_folder_creates_a_document_and_sets_the_version_comment()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var folderName = $"dropfolder-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == folderName);

        var docName = $"dropped-{Guid.NewGuid():N}.txt";
        var newId = await api.UploadFileAsync(folder.Id, docName, Encoding.UTF8.GetBytes("body"), "filed via drop");

        // Find the created row and follow ITS advertised versions rel — upload returns an id, and an id is not
        // an address (ADR 0543).
        var created = (await api.GetChildrenAsync(folder.Id)).Single(n => n.Id == newId);
        // The filing comment lands on the created document's first version (ADR 0528), not a chat comment.
        var versions = await api.GetVersionsAsync(created.Href("versions"));
        Assert.Equal("filed via drop", versions.Single(v => v.VersionNumber == 1).Comment);
    }
}
