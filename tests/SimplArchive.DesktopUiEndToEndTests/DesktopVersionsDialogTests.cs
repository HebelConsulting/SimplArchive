using System.Text;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop Versions dialog (ADR "Versions dialog") at the VM level over the real api client: a document with
// two versions lists both (the latest marked current), and "Make current" on the older one pins it via the
// CurrentVersionId pointer (issue #265) — no new version, the older one is simply flagged current.
[Collection(UiCollection.Name)]
public class DesktopVersionsDialogTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopVersionsDialogTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Lists_versions_and_makes_one_current()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"vers-{Guid.NewGuid():N}.txt";
        await api.Documents.UploadFileAsync(repo.Href("children"), name, Encoding.UTF8.GetBytes("one\n"));
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));
        await api.Documents.UploadNewVersionAsync(doc.Href("versions"), Encoding.UTF8.GetBytes("two\n"), ".txt");

        var vm = new VersionsViewModel();
        await vm.SetupAsync(api, doc.Id, doc.Name, doc.Href("versions"));

        // Two versions, newest first, the latest labelled current.
        Assert.Equal(2, vm.Versions.Count);
        Assert.True(vm.HasMultiple);
        Assert.Equal(2, vm.Versions[0].VersionNumber);
        Assert.True(vm.Versions[0].IsCurrent);
        var v1 = vm.Versions.Single(r => r.VersionNumber == 1);
        Assert.False(v1.IsCurrent);
        Assert.True(v1.CanMakeCurrent);

        // Make-current is single-select-gated: enabled only for a selected, non-current row.
        Assert.False(vm.CanMakeCurrentSelected);              // nothing selected yet
        vm.SelectedVersion = vm.Versions[0];                   // the current version
        Assert.False(vm.CanMakeCurrentSelected);              // can't make the current one current
        vm.SelectedVersion = v1;                               // an older version
        Assert.True(vm.CanMakeCurrentSelected);

        // Make v1 current → the pointer pins the existing v1 (no new version): the list stays at two, v1 is now
        // flagged current, v2 no longer is, and the dialog is marked changed.
        // (The confirmation lives in the view; the VM command performs the pointer set.)
        await ((IAsyncRelayCommand)vm.MakeCurrentCommand).ExecuteAsync(v1);
        Assert.True(vm.Changed);
        Assert.Equal(2, vm.Versions.Count);
        Assert.True(vm.Versions.Single(r => r.VersionNumber == 1).IsCurrent);
        Assert.False(vm.Versions.Single(r => r.VersionNumber == 2).IsCurrent);
    }
}
