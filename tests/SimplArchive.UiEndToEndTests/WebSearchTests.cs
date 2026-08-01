using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow: the Search tab finds the seeded document by name and clicking the result navigates to it on the
// Repositories tab. (The fixture has no OpenSearch, so this exercises the Postgres metadata-search fallback,
// which matches the document name immediately — no indexing delay.)
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSearchTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSearchTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Searching_finds_the_document_and_clicking_the_result_navigates_to_it()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab").Filter(new() { HasText = "Search" }).First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");

        var result = page.Locator(".wb-search-results .wb-list-row").Filter(new() { HasText = "Invoice 2025-001" });
        await Expect(result).ToBeVisibleAsync();
        await result.First.ClickAsync();

        // Navigates to the Repositories tab and selects the document — its name shows in the detail pane (the
        // preview is a pdf.js canvas for the seeded invoice PDF, so assert on the index detail, not preview text).
        await Expect(page.Locator(".wb-tab-active")).ToContainTextAsync("Repositories");
        await Expect(page.Locator(".wb-sysfields")).ToContainTextAsync("Invoice 2025-001");
    }
}
