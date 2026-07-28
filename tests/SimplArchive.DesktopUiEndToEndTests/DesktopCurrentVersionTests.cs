using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The document's current (latest confirmed) version number surfaced by GetSystemFieldsAsync — the source of the
// detail pane's last line (ADR "Mask-pane current-version line"). A fresh upload is version 1; a new version
// bumps it to 2.
[Collection(UiCollection.Name)]
public class DesktopCurrentVersionTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopCurrentVersionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task System_fields_carry_the_current_version_number()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var folderName = $"curver-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == folderName);

        var docName = $"curverdoc-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(folder.Id, docName, Encoding.UTF8.GetBytes("v1"));
        var doc = (await api.GetChildrenAsync(folder.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(docName));

        var v1 = await api.GetSystemFieldsAsync(doc.Id);
        Assert.NotNull(v1);
        Assert.Equal(1, v1!.CurrentVersionNumber);

        await api.UploadNewVersionAsync(doc.Id, Encoding.UTF8.GetBytes("v2"), ".txt");
        var v2 = await api.GetSystemFieldsAsync(doc.Id);
        Assert.Equal(2, v2!.CurrentVersionNumber);
    }
}
