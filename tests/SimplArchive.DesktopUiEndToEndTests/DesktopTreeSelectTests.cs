using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop bugfix (ADR "Desktop recycle bin parity"): selecting a folder in the tree pane updates the contents
// (list) pane — including re-tapping the already-selected node after drilling into a subfolder via the list.
// Drilling moves the list without moving the tree's selection, so the [ObservableProperty] SelectedTreeNode
// setter short-circuits a same-reference re-selection; MainWindowViewModel.ReselectTreeFolderAsync (the Tapped
// handler's target) reloads the list in that case. Driven through the real desktop VM against the running Api.
[Collection(UiCollection.Name)]
public class DesktopTreeSelectTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTreeSelectTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Re_tapping_a_tree_folder_after_drilling_via_the_list_reloads_the_list()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (afterDrill, afterRetap, items) = await vm.TreeReselectSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Drilling into the subfolder via the contents list changed the shown folder…
        Assert.NotEqual(afterDrill, afterRetap);
        // …and re-tapping the still-selected repository node in the tree reloaded the list back to the repo,
        // re-listing its children (the bug: this stayed on the subfolder because the re-selection was a no-op).
        Assert.Equal(vm.Tree[0].Id, afterRetap);
        Assert.NotEmpty(items);
    }
}
