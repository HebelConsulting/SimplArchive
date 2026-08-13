using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The Search tab as a place to work rather than a lookup that hands you back to the tree (#462): select a result
// and it previews beside the list, × clears the text, and Reset clears everything.
//
// The reset case is the one worth the run time. "Clear filters" used to reset the refinement panel and leave the
// FACET drill-downs applied, so the form looked empty while the results stayed narrowed by criteria shown nowhere
// — a bug that reads as "search is wrong" and that no test could see, because every assertion about filters
// looked at the panel rather than at what came back.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSearchWorkbenchTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSearchWorkbenchTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Selecting_a_result_previews_it_without_leaving_the_tab()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");

        var result = page.Locator(".wb-search-results .wb-list-row").Filter(new() { HasText = "Invoice 2026-003" });
        await Expect(result).ToBeVisibleAsync();
        await result.First.ClickAsync();

        // `.wb-pv-page` is what pdf.js emits per rendered page, so it is present only if the preview really ran —
        // the seeded invoice is a canvas with no text to match, so anything weaker would pass without a preview.
        await Expect(page.Locator(".wb-search-preview .wb-pv-page").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // And the hits are MARKED on it. This is the assertion that makes the test about previewing a *hit*
        // rather than previewing a document: `.wb-pv-hit` is emitted per match by preview.js, and it appears only
        // when the pane was seeded with the query. Without this line, dropping seedQuery — the whole payoff of
        // item 5, and a parameter that had no caller before #462 — still passed.
        await Expect(page.Locator(".wb-search-preview .wb-pv-hit").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // And it stayed here. The whole point of previewing in the tab is not being thrown back to the tree to
        // look at a hit.
        await Expect(page.Locator(".wb-tab-active")).ToHaveAttributeAsync("aria-label", "Search");
    }

    [Fact]
    public async Task The_clear_button_empties_the_text_and_reset_empties_everything()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");
        await Expect(page.Locator(".wb-search-results .wb-list-row").First).ToBeVisibleAsync();

        // Open the refinement panel and narrow by a created-by value, so there is a filter for Reset to clear
        // that is NOT the text.
        await page.GetByRole(AriaRole.Button, new() { Name = "Filters" }).First.ClickAsync();
        var createdBy = page.Locator(".wb-search-filters input").Last;
        await FillMudAsync(createdBy, "Demo Admin");

        // × clears the text only — the filter it does not touch is still there afterwards.
        await input.PressAsync("Escape");
        await page.Locator(".wb-search-bar button").First.ClickAsync();
        await Expect(input).ToHaveValueAsync("");
        await Expect(createdBy).ToHaveValueAsync("Demo Admin");

        // Reset clears everything, including that filter.
        await page.GetByRole(AriaRole.Button, new() { Name = "Reset search criteria" }).ClickAsync();
        await Expect(input).ToHaveValueAsync("");
        await Expect(createdBy).ToHaveValueAsync("");
    }

    // MudTextField (no Immediate) commits on BLUR, so filling without blurring leaves the bound value unset and
    // the assertions below would be testing the DOM rather than the component's state.
    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.EvaluateAsync("el => el.blur()");
    }
}
