using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Issue #340: double-clicking a document search hit must reveal it in context — expand + select its parent folder
// in the tree pane, load that folder into the list pane, and select the document there (preview already worked).
// Driven through the real desktop VM (OpenSearchResultAsync) against the running Api, seeding a nested doc so the
// reveal walks a real ancestor chain (repository → subfolder → document).
[Collection(UiCollection.Name)]
public class DesktopSearchRevealTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSearchRevealTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Activating_a_document_hit_reveals_the_parent_folder_and_selects_the_document()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (treeSelectedParent, listHasDoc, listSelectedDoc) =
            await vm.SearchRevealSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(treeSelectedParent, "the document's parent folder should be selected in the tree pane");
        Assert.True(listHasDoc, "the document should be listed in the list pane");
        Assert.True(listSelectedDoc, "the document should be selected in the list pane");
    }
}
