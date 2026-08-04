using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop inbox send + admin triage (ADR 0532): an own item can be handed to another user, and a CanManageInboxes
// holder can open that user's inbox via ?user=. Exercised at the VM level over the real Api (the dialog gesture is
// view-only) — InboxSendSelfTestAsync sends a fresh item to a new user and confirms it moved.
[Collection(UiCollection.Name)]
public class DesktopInboxSendTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopInboxSendTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Sending_an_own_item_hands_it_to_another_user()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var moved = await vm.InboxSendSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(moved);
    }
}
