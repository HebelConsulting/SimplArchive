using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Issue #339: the repository sort order — folders always come first, sorted alphabetically (independent of the
// per-folder document sort), then documents by the active criterion (default DocumentDate); the tree's folder
// children are alphabetical too. Driven through the real desktop VM against the running Api.
[Collection(UiCollection.Name)]
public class DesktopRepositorySortTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopRepositorySortTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Folders_come_first_alphabetically_in_both_the_list_and_the_tree()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (listFoldersAlphaThenDoc, treeFoldersAlpha) =
            await vm.RepositorySortSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(listFoldersAlphaThenDoc, "list pane: folders alphabetical first, then documents");
        Assert.True(treeFoldersAlpha, "tree pane: folder children sorted alphabetically");
    }
}
