using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Per-folder contents sort order (ADR "Per-folder contents sort order"): a folder's persisted default order
// (Name / Document date / Created) round-trips through the real VM + api client, and the detail-pane Save path
// updates the VM state. Column-header sorting stays an ephemeral override.
[Collection(UiCollection.Name)]
public class DesktopFolderContentsSortTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopFolderContentsSortTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Folder_sort_order_defaults_to_document_date_and_round_trips()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var ok = await vm.FolderContentsSortSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(ok);
    }
}
