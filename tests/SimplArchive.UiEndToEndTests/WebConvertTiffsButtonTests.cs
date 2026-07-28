using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0293 / "Scanned image-only PDF detection"): the tenant-admin "Convert scans" ribbon button.
// The demo tenant has no TIFF or scanned-PDF documents (the seeded document is a .txt), so clicking it reports
// "No documents need conversion" — exercising the admin-gated button + the pending-count path without
// enqueuing anything.
[Collection(UiCollection.Name)]
public class WebConvertTiffsButtonTests
{
    private readonly SelfHostedAppFixture _app;

    public WebConvertTiffsButtonTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Convert_scans_reports_nothing_to_convert()
    {
        var page = await Ui.LoginAsync(_app);

        // The button is present for the tenant admin (whoami.isTenantAdmin) on the Repositories ribbon.
        await page.Locator(".wb-ribbon").GetByText("Convert scans").First.ClickAsync();

        await Expect(page.GetByText("No documents need conversion.")).ToBeVisibleAsync();
    }
}
