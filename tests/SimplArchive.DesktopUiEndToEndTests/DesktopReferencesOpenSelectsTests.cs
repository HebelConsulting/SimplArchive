using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// References dialog "Open" bugfix: opening a document's primary location OR a referencing folder from the
// references dialog must open that folder AND select the document for viewing — the real row at the primary
// location, and the reference (shortcut) row in a referencing folder (which was previously left unselected
// because the selection filtered references out). Driven through the real desktop VM against the Api.
[Collection(UiCollection.Name)]
public class DesktopReferencesOpenSelectsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopReferencesOpenSelectsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Opening_primary_and_referencing_folders_selects_the_document()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (selectedInPrimary, selectedReferenceInRefFolder) =
            await vm.OpenReferenceSelectsDocumentSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(selectedInPrimary, "opening the primary location should select the document's real row");
        Assert.True(selectedReferenceInRefFolder, "opening a referencing folder should select the document's reference row");
    }
}
