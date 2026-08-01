using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0293 / "Scanned image-only PDF detection"): the tenant-admin "Convert scans" ribbon button.
// The demo tenant's seeded PDFs (the invoice + the offer's revisions, ADR 0502) are born-digital, but the pending
// count is an upper bound over *all* latest-confirmed PDFs (the worker's per-document detection later skips the
// born-digital ones), so clicking the button surfaces the "queue N scanned document(s)" confirm dialog — exercising
// the admin-gated button + the pending-count path.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebConvertTiffsButtonTests
{
    private readonly SelfHostedAppFixture _app;

    public WebConvertTiffsButtonTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Convert_scans_offers_to_queue_the_pdf_candidates()
    {
        var page = await Ui.LoginAsync(_app);

        // The button is present for the tenant admin (whoami.isTenantAdmin) on the Repositories ribbon.
        await page.Locator(".wb-ribbon").GetByText("Convert scans").First.ClickAsync();

        // The seeded PDFs are candidates, so the confirm dialog appears (rather than the empty-state snackbar).
        await Expect(page.Locator(".mud-dialog")).ToContainTextAsync("scanned document");
    }
}
