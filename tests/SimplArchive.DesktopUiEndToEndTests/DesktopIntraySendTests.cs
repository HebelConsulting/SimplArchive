using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop intray send + admin triage (ADR 0532): an own item can be handed to another user, and a CanManageIntrayes
// holder can open that user's intray via ?user=. Exercised at the VM level over the real Api (the dialog gesture is
// view-only) — IntraySendSelfTestAsync sends a fresh item to a new user and confirms it moved.
[Collection(UiCollection.Name)]
public class DesktopIntraySendTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopIntraySendTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Sending_an_own_item_hands_it_to_another_user()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var moved = await vm.IntraySendSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(moved);
    }
}
