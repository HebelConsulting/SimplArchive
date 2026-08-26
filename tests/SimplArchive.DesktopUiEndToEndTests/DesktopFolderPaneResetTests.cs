using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop bugfix (ADR 0516): selecting a *different* folder in the tree/list resets the panes right of the list
// (index-data / preview / comments) so they don't keep showing the previously-viewed document — parity with the
// web client. A same-folder reload (after an in-place operation) deliberately keeps the current detail. Driven
// through the real desktop VM against the Api.
//
// What "reset" MEANS changed with ADR 0703 and this test moved with it: the pane no longer goes blank on a
// folder change, it describes the folder just opened. The previously-viewed document not surviving the move is
// the invariant; being empty was only how that used to look, and asserting emptiness now would pin the
// behaviour ADR 0703 replaced.
//
// Only the negative half is asserted here — the sentinel is gone. The positive half (the pane names the folder
// just opened) needs a breadcrumb trail, and this self-test loads contents by id and links without going
// through the tree, so it never builds one; DesktopTreeMarkTests asserts it through the real path instead.
// Which is worth stating plainly, because it says what this guard was worth before: the ORIGINAL assertion —
// that the title was empty — passed because this path never built a title, not because anything cleared it.
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

        Assert.True(clearedOnFolderChange, "moving to a different folder should drop the previous subject and describe the new folder");
        Assert.True(keptOnSameFolderReload, "a same-folder reload should keep the current detail");
    }
}
