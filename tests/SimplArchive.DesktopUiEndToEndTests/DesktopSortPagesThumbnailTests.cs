using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The sort dialog's page pictures (#522): InboxPageThumbnails is the desktop's whole supply line for them, and
// it had no test — which is how "the sort dialog comes up empty" shipped. The dialog itself is deliberately
// dumb (it renders the bitmaps it is handed), so pages arriving is the whole question — for both formats and
// both of their routes: PDF rasterised locally by PDFium, TIFF via the server's preview-pages renditions.
//
// Driven through the client's own `--sort-thumbs-test` headless hook AS A SUBPROCESS, not in-process: the
// pipeline decodes into Avalonia bitmaps, and a bare test process has no render platform — in-process, every
// load dies on a missing IPlatformRenderInterface, which reproduces nothing about the product. The hook runs
// on the same headless Avalonia + Skia platform the screenshot hooks use, hands the result to the REAL
// SortPagesDialog, and prints both counts, so a failure names which half lost the pages.
//
// The fixtures are the checked-in sample batches the app itself serves under /download/samples/ — real
// multi-page files, one PDF page deliberately upside-down.
[Collection(UiCollection.Name)]
public class DesktopSortPagesThumbnailTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSortPagesThumbnailTests(SelfHostedAppFixture app) => _app = app;

    [Theory]
    [InlineData("SimplArchive-Patch3-Sample-Batch.pdf", 7)]
    [InlineData("SimplArchive-Patch3-Sample-Scan.tif", 7)]
    public async Task Thumbnails_load_for_every_page_of_a_staged_item(string sample, int pages)
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new SimplArchiveApiClient(token);

        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync($"{_app.BaseUrl}/download/samples/{sample}");

        var name = $"sort-{Guid.NewGuid():N}{Path.GetExtension(sample)}";
        await api.Inbox.UploadAsync(name, bytes);

        var (exitCode, output) = await DesktopProc.RunAsync(
            "--sort-thumbs-test", token, name, _app.BaseUrl);

        Assert.True(exitCode == 0,
            $"sort-thumbs-test exited {exitCode}:\n{output}\n---- api log tail ----\n{string.Join('\n', _app.ApiLog().Split('\n')[^40..])}");
        Assert.Contains($"SORT-THUMBS loaded={pages} dialog={pages} rotations=ok", output);
    }
}
