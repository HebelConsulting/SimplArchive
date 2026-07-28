using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop tree-pane UX (ADR "Desktop tree-pane folder context menu"): the folder context menu's actions
// (create subfolder / rename / delete) work end-to-end through the real VM against the running Api, and a
// tree node lazy-loads its children when expanded (backing the single-click-to-expand behavior).
[Collection(UiCollection.Name)]
public class DesktopTreeFolderActionsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTreeFolderActionsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Create_rename_delete_a_folder_from_the_tree()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (created, renamed, deleted) = await vm.TreeFolderActionsSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(created);
        Assert.True(renamed);
        Assert.True(deleted);
    }

    [Fact]
    public async Task Expanding_a_tree_node_lazy_loads_its_children()
    {
        var loaded = false;
        var node = new TreeNodeViewModel(Guid.NewGuid(), "Folder", hasSubfolders: true, _ =>
        {
            loaded = true;
            return Task.FromResult<IEnumerable<TreeNodeViewModel>>([new TreeNodeViewModel(Guid.NewGuid(), "Child", false, null)]);
        });

        node.IsExpanded = true; // what a single click now does
        for (var i = 0; i < 50 && !loaded; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(loaded);
        Assert.Contains(node.Children, c => c.Name == "Child");
    }
}
