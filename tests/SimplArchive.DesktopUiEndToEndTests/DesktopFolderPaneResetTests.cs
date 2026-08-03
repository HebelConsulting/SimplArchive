using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop bugfix (ADR 0516): selecting a *different* folder in the tree/list resets the panes right of the list
// (index-data / preview / comments) so they don't keep showing the previously-viewed document — parity with the
// web client, whose SelectFolderAsync clears the detail on every folder selection. A same-folder reload (after an
// in-place operation) deliberately keeps the current detail. Driven through the real desktop VM against the Api.
[Collection(UiCollection.Name)]
public class DesktopFolderPaneResetTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopFolderPaneResetTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Navigating_to_a_different_folder_clears_the_detail_panes_but_a_same_folder_reload_keeps_them()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (clearedOnFolderChange, keptOnSameFolderReload) =
            await vm.FolderChangeResetsPanesSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(clearedOnFolderChange, "moving to a different folder should reset the right-of-list panes");
        Assert.True(keptOnSameFolderReload, "a same-folder reload should keep the current detail");
    }
}
