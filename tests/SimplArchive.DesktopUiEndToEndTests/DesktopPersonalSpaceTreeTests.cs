using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The GUI-tree Personal space grouping (ADR "GUI-tree Personal space grouping"): the desktop Personal node nests
// the Inbox + Check-out launcher nodes above its real subfolders (mirroring /webdav/Personal), and selecting a
// launcher switches to the matching bottom tab — driven through the real VM against the running Api.
[Collection(UiCollection.Name)]
public class DesktopPersonalSpaceTreeTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopPersonalSpaceTreeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Personal_node_nests_inbox_and_checkout_launchers_that_switch_tabs()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var log = await vm.PersonalLaunchersSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.All(log, line => Assert.StartsWith("OK", line));
    }

    [Fact]
    public void Launcher_node_carries_the_right_icon_and_target_tab()
    {
        var inbox = new TreeNodeViewModel(Guid.Empty, "Inbox", false, null, personalKind: "inbox");
        var checkout = new TreeNodeViewModel(Guid.Empty, "Check-out", false, null, personalKind: "checkout");

        Assert.True(inbox.IsLauncher);
        Assert.Equal(1, inbox.LauncherTab);
        Assert.Equal("mdi-inbox-arrow-down", inbox.IconValue);

        Assert.True(checkout.IsLauncher);
        Assert.Equal(2, checkout.LauncherTab);
        Assert.Equal("mdi-lock-open-variant-outline", checkout.IconValue);
    }
}
