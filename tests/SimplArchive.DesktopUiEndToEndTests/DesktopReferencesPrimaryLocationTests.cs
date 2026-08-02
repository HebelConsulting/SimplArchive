using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop api-client's references view + promote (ADR 0506): GetReferencesViewAsync surfaces the item's real
// primary location alongside its referencing folders, and SetPrimaryLocationAsync re-homes the document into a
// referenced folder while leaving a reference at the former home (and dropping the redundant target-side one).
[Collection(UiCollection.Name)]
public class DesktopReferencesPrimaryLocationTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopReferencesPrimaryLocationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Promote_moves_the_document_and_leaves_a_reference_behind()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var nameA = $"pl-a-{Guid.NewGuid():N}";
        var nameB = $"pl-b-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, nameA);
        await api.CreateFolderAsync(repo.Id, nameB);
        var folderA = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == nameA);
        var folderB = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == nameB);

        var docName = $"pl-doc-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(folderA.Id, docName, Encoding.UTF8.GetBytes("body"));
        var doc = (await api.GetChildrenAsync(folderA.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(docName));

        // A pre-existing reference to the doc in Folder B — promotion should drop it as redundant.
        await api.CreateReferenceAsync(folderB.Id, doc.Id);

        var before = await api.GetReferencesViewAsync(doc.Id);
        Assert.Equal(folderA.Id, before.Primary!.Id);
        Assert.Contains(before.Folders, f => f.Id == folderB.Id);

        await api.SetPrimaryLocationAsync(doc.Id, folderB.Id);

        var after = await api.GetReferencesViewAsync(doc.Id);
        Assert.Equal(folderB.Id, after.Primary!.Id);                     // re-homed into Folder B
        Assert.Contains(after.Folders, f => f.Id == folderA.Id);         // reference left at the former home
        Assert.DoesNotContain(after.Folders, f => f.Id == folderB.Id);   // redundant reference removed
    }
}
