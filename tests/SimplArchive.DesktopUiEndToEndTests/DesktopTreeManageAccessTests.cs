using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the tree-pane context menu's Manage-access action (ADR "Tree-pane context menu with
// manage-access"). The ACL dialog itself is already covered by DesktopManageAccessTests; what's new here is the
// TREE as the entry point — in particular a repository ROOT, which never appears as a contents-list row, so the
// list-row context menu could never manage its permissions.
[Collection(UiCollection.Name)]
public class DesktopTreeManageAccessTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTreeManageAccessTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Manage_access_works_on_a_repository_root_and_a_subfolder_from_the_tree()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (rootGranted, subfolderGranted) = await vm.TreeManageAccessSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(rootGranted);
        Assert.True(subfolderGranted);
    }

    [Fact]
    public async Task Move_and_place_reference_act_on_the_right_clicked_tree_folder()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (moved, referenced) = await vm.TreeFolderMoveAndReferenceSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(moved);
        Assert.True(referenced);
    }
}
