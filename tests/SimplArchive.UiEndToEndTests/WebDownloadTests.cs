using Microsoft.Playwright;

namespace SimplArchive.UiEndToEndTests;

// Task 2 (web half): the web client's Download action opens the document's presigned download URL (attachment
// disposition, named after the document). Drives the real workbench end to end.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebDownloadTests
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

        // Ribbon Download calls window.open(downloadUrl) — a presigned URL whose response-content-disposition names
        // the file "Invoice 2025-001.pdf" as an attachment. Capture the opened URL (independent of how the browser
        // then handles it: SeaweedFS ignores the attachment override, so a PDF would open inline rather than firing
        // a browser "download" event). The actual byte download is covered server-side by the E2E DocumentDownloadTests.
        await page.EvaluateAsync("() => { window.__openedUrl = null; window.open = (u) => { window.__openedUrl = u; return null; }; }");
        // Ribbon buttons are icon-only (#305) — select by aria-label, not the now-hidden text.
        await page.Locator(".wb-ribbon [aria-label=\"Download\"]").First.ClickAsync();
        await page.WaitForFunctionAsync("() => window.__openedUrl !== null", null, new() { Timeout = 15000 });

        var url = Uri.UnescapeDataString(await page.EvaluateAsync<string>("() => window.__openedUrl"));
        // The download filename rides the response-content-disposition (RFC 5987 filename*=…, so the space stays
        // %20-encoded after one decode); assert on the intact tail + the attachment disposition.
        Assert.Contains("2025-001.pdf", url);
        Assert.Contains("attachment", url);
    }
}
