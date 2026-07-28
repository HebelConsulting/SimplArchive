using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop inbox file-list drop-zone (ADR "Inbox file-list drop-zone"): dropping OS files onto the inbox
// list uploads them into the S3-backed inbox. Exercised at the VM level over the real Api (the drop gesture
// itself is view-only) — UploadFilesToInboxAsync puts the dropped bytes into the server inbox.
[Collection(UiCollection.Name)]
public class DesktopInboxDropTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopInboxDropTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Dropping_files_uploads_them_to_the_inbox()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var uploaded = await vm.InboxDropSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(uploaded);
    }
}
