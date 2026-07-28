using System.Text;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop Versions dialog (ADR "Versions dialog") at the VM level over the real api client: a document with
// two versions lists both (the latest marked current), and "Make current" on the older one reinstates it as a
// new current version (a third row).
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

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"vers-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("one\n"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));
        await api.UploadNewVersionAsync(doc.Id, Encoding.UTF8.GetBytes("two\n"), ".txt");

        var vm = new VersionsViewModel();
        await vm.SetupAsync(api, doc.Id, doc.Name);

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

        // Make v1 current → a new version is created, so the list grows to three and it's marked changed.
        // (The confirmation lives in the view; the VM command performs the restore.)
        await ((IAsyncRelayCommand)vm.MakeCurrentCommand).ExecuteAsync(v1);
        Assert.True(vm.Changed);
        Assert.Equal(3, vm.Versions.Count);
        Assert.Equal(3, vm.Versions[0].VersionNumber);
        Assert.True(vm.Versions[0].IsCurrent);
    }
}
