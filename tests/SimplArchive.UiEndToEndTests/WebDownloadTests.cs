using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SimplArchive.UiEndToEndTests;

// Task 2 (web half): the web client's Download actually downloads the document — the browser saves a file named
// after the document with the correct bytes. Drives the real workbench end to end.
[Collection(UiCollection.Name)]
public partial class WebDownloadTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDownloadTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Downloads_the_selected_document()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");

        // Navigate the seeded tree: Demo Repository → Invoices → Invoice 2025-001.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await list.GetByText("Invoices").First.DblClickAsync();
        await list.GetByText("Invoice 2025-001").First.ClickAsync();

        // Ribbon Download → the browser download.
        var download = await page.RunAndWaitForDownloadAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText(DownloadRegex()).First.ClickAsync();
        });

        Assert.Contains("Invoice 2025-001", download.SuggestedFilename);

        var path = await download.PathAsync();
        Assert.NotNull(path);
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path!));
        Assert.Contains("Invoice 2025-001", text); // the seeded invoice content
    }

    [GeneratedRegex("^download$", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadRegex();
}
