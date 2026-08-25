using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0264): the Search tab's refinement Filters panel actually narrows results. The fixture runs
// OpenSearch (the Postgres fallback ignores these filters), so a "created by" system filter that matches vs.
// doesn't-match toggles the seeded document in/out of the results. Indexing is async (startup reindex), so the
// initial search polls.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebSearchFilterTests
{
    private const string Doc = "Invoice 2026-003";

    private readonly SelfHostedAppFixture _app;

    public WebSearchFilterTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Created_by_filter_narrows_the_search_results()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        var result = page.Locator(".wb-search-results .wb-list-row").Filter(new() { HasText = Doc });

        // Poll until the document is indexed (startup reindex is async).
        for (var i = 0; i < 30; i++)
        {
            await RunSearchAsync(input);
            if (await result.IsVisibleAsync())
            {
                break;
            }

            await Task.Delay(1000);
        }

        await Expect(result).ToBeVisibleAsync();

        // Open the Filters panel and add a matching "created by" filter → still found.
        await page.GetByRole(AriaRole.Button, new() { Name = "Filters" }).ClickAsync();
        var createdBy = page.Locator("input[placeholder*='name contains']");
        await FillMudAsync(createdBy, "Demo"); // the document's creator is "Demo Admin"
        await RunSearchAsync(input);
        await Expect(result).ToBeVisibleAsync();

        // A non-matching "created by" filter → excluded. Poll until the result clears: the filter query and the
        // results re-render are async (OpenSearch + Blazor), so on a loaded runner the stale matching-search row
        // can still be showing when a single-shot negative assertion gives up — the source of an intermittent
        // "expected not to be visible" flake. Mirrors the indexing poll above.
        await FillMudAsync(createdBy, "Nobody");
        for (var i = 0; i < 30; i++)
        {
            await RunSearchAsync(input);
            if (!await result.IsVisibleAsync())
            {
                break;
            }

            await Task.Delay(1000);
        }

        await Expect(result).Not.ToBeVisibleAsync();
    }

    private static async Task RunSearchAsync(ILocator input)
    {
        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");
    }

    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.EvaluateAsync("el => el.blur()"); // MudTextField commits on blur (no Immediate)
    }
}
