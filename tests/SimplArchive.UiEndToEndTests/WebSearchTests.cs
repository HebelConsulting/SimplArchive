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

        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");

        var result = page.Locator(".wb-search-results .wb-list-row").Filter(new() { HasText = "Invoice 2026-003" });
        await Expect(result).ToBeVisibleAsync();
        // Select the row (a plain click SELECTS and previews, #462), then the TOOLBAR's Go to — the visible
        // route since #530 tranche 8 moved the per-row button into the touch-only ⋮ menu.
        await result.First.ClickAsync();
        await page.Locator(".wb-search-bar").GetByRole(AriaRole.Button, new() { Name = "Go to document" }).ClickAsync();

        // Navigates to the Repositories tab and selects the document — its name shows in the detail pane (the
        // preview is a pdf.js canvas for the seeded invoice PDF, so assert on the index detail, not preview text).
        await Expect(page.Locator(".wb-tab-active")).ToHaveAttributeAsync("aria-label", "Repositories");
        await Expect(page.Locator(".wb-sysfields")).ToContainTextAsync("Invoice 2026-003");
    }

    // Opening a hit must actually RENDER the document, not just select it.
    //
    // The existing test above asserts the index detail, because the seeded invoice is a pdf.js canvas with no
    // text to match — which means it passes whether or not the preview loaded. That blind spot became a real
    // regression when each tab got its own PreviewPane (ADR 0558): the pane's @ref is assigned after a render,
    // and this path calls SetTab(Repositories) and selects the document in one synchronous run, so the pane
    // does not exist yet at the moment the preview is requested. Rendering into the null ref lost the preview
    // silently on the two commonest cross-tab paths — a search hit and a notification click.
    //
    // `.wb-pv-page` is what pdf.js emits per rendered page, so it is present only if the preview really ran.
    [Fact]
    public async Task Opening_a_search_hit_renders_its_preview_not_just_its_details()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");

        var result = page.Locator(".wb-search-results .wb-list-row").Filter(new() { HasText = "Invoice 2026-003" });
        await Expect(result).ToBeVisibleAsync();
        await result.First.ClickAsync(); // select — the toolbar's Go to acts on the selection (#530 tranche 8)
        await page.Locator(".wb-search-bar").GetByRole(AriaRole.Button, new() { Name = "Go to document" }).ClickAsync();

        await Expect(page.Locator(".wb-tab-active")).ToHaveAttributeAsync("aria-label", "Repositories");
        await Expect(page.Locator(".wb-pv-page").First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
    }

    // The results must survive leaving the tab and coming back.
    //
    // This is not a hypothetical: opening a hit switches to Repositories (the test above), so "search → open a
    // hit → back for the next one" is the ordinary way search is used, and it crosses the tab boundary every
    // time. When the workbench page was decomposed (ADR 0558) the Search tab became a component that is DISPOSED
    // whenever another tab is shown, which would have emptied the results at exactly that moment — so the state
    // lives in the injected SearchState instead, mirroring the desktop client, where it sits on the
    // MainWindowViewModel and outlives every tab switch (ADR 0511). Nothing else asserts that, and the failure
    // would look like ordinary forgetfulness rather than a bug.
    [Fact]
    public async Task Search_results_survive_switching_to_another_tab_and_back()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");

        var result = page.Locator(".wb-search-results .wb-list-row").Filter(new() { HasText = "Invoice 2026-003" });
        await Expect(result).ToBeVisibleAsync();

        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await Expect(page.Locator(".wb-tab-active")).ToHaveAttributeAsync("aria-label", "Repositories");

        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        // The whole tab comes back as it was left: the hit still in the list, the status line, and the query
        // still in the box — not a blank tab the user has to search again from. Results are asserted FIRST
        // because they are the point; the query box merely reflects them.
        await Expect(result).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-search-status")).ToContainTextAsync("result(s)");
        await Expect(input).ToHaveValueAsync("Invoice");
    }
}
