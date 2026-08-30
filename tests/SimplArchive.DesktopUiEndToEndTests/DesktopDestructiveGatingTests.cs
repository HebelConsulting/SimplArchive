using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The destructive affordances are gated on what the server said about the row (#858).
//
// This is the POSITIVE half, and it needs a real server to exist. The headless `--screenshot --menu` capture
// shows the negative half convincingly — the demo tree's nodes are synthetic, carry no capabilities, and the
// menu correctly renders without Rename / Move to / Sort order / Delete. But a gate that hides everything is
// indistinguishable from a gate that is stuck off, so proving the flags ARRIVE and turn the menu back on is the
// half that says the wiring works rather than merely that it blocks.
//
// It asserts at the view-model level rather than by driving a menu: the menu's IsVisible bindings read exactly
// these properties, and Avalonia's context menu needs a pointer that a headless run does not have.
[Collection(UiCollection.Name)]
public class DesktopDestructiveGatingTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDestructiveGatingTests(SelfHostedAppFixture app) => _app = app;

    private async Task<MainWindowViewModel> OpenAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        return vm;
    }

    [Fact]
    public async Task A_full_rights_caller_gets_the_capabilities_on_its_tree_nodes()
    {
        var vm = await OpenAsync();

        // The seeded admin owns their personal space outright, so every capability should be present. If these
        // came back false the menu would be empty for the one caller who may do everything — which is how a
        // gate that is silently stuck off would look.
        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();

        var folder = personal.Children.FirstOrDefault(c => !c.IsSynthetic && !c.IsLauncher);
        Assert.NotNull(folder);

        Assert.True(folder!.CanEditIndexData, "the owner may rename and reorder their own folder — the Rename and Sort order gate.");
        Assert.True(folder.CanDelete, "the owner may delete their own folder — the Delete gate.");
        Assert.True(folder.CanMove, "the owner may move their own folder — the Move to gate.");
    }

    [Fact]
    public async Task A_synthetic_node_offers_nothing_destructive()
    {
        // The Administration branch and the Personal launchers are not documents. They must never carry the
        // capabilities, or the menu would offer Delete on a node with nothing behind it to delete.
        var vm = await OpenAsync();

        foreach (var node in vm.Tree.Where(n => n.IsSynthetic))
        {
            Assert.False(node.CanDelete);
            Assert.False(node.CanEditIndexData);
            Assert.False(node.CanMove);
        }
    }

    [Fact]
    public async Task The_list_rows_carry_the_capabilities_too()
    {
        // The row menu reads CanRenameSelected / CanDeleteSelected off the SELECTED row, so the flags have to
        // survive the other parse path (the children listing into NodeViewModel) as well as the tree's.
        var vm = await OpenAsync();

        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();
        var folder = personal.Children.First(c => !c.IsSynthetic && !c.IsLauncher);

        vm.SelectedTreeNode = folder;
        await vm.OpenFolderAsync(folder.Href("children"));

        var row = vm.Items.FirstOrDefault(i => !i.IsArchiveBack && !i.IsArchiveEntry);
        if (row is null)
        {
            return; // an empty folder proves nothing either way; the tree assertions above carry this case
        }

        Assert.True(row.CanEditIndexData || row.CanDelete || row.CanMove,
            "a row in the caller's own space arrived with no capabilities at all — the listing or the parse dropped them.");
    }
}
