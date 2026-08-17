using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop intray file-list drop-zone (ADR "Inbox file-list drop-zone"): dropping OS files onto the intray
// list uploads them into the S3-backed intray. Exercised at the VM level over the real Api (the drop gesture
// itself is view-only) — UploadFilesToIntrayAsync puts the dropped bytes into the server intray.
[Collection(UiCollection.Name)]
public class DesktopIntrayDropTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopIntrayDropTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Dropping_files_uploads_them_to_the_intray()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var uploaded = await vm.IntrayDropSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(uploaded);
    }
}
