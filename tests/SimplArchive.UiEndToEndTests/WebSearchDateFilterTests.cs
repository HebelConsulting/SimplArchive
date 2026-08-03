using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0264/0258): a document-date range filter narrows the search. Setting "document date from" to a
// future month excludes the seeded document (whose document date is today). OpenSearch is in the fixture;
// indexing is async, so the initial search polls.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSearchDateFilterTests
{
    private const string Doc = "Invoice 2025-001";

    private readonly SelfHostedAppFixture _app;

    public WebSearchDateFilterTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Document_date_from_filter_narrows_the_results()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        var result = page.Locator(".wb-search-results .wb-list-row").Filter(new() { HasText = Doc });

        for (var i = 0; i < 30; i++)
        {
            await input.FillAsync("Invoice");
            await input.PressAsync("Enter");
            if (await result.IsVisibleAsync())
            {
                break;
            }

            await Task.Delay(1000);
        }

        await Expect(result).ToBeVisibleAsync();

        // Set "document date from" to a future month (next month, day 15) → the seeded doc (today) is excluded.
        await page.GetByRole(AriaRole.Button, new() { Name = "Filters" }).ClickAsync();
        var docDateRow = page.Locator(".wb-filter-row").Filter(new() { HasText = "Document date" });
        await docDateRow.Locator("input[placeholder='from']").ClickAsync(); // open the calendar

        await page.Locator(".mud-picker-nav-button-next").ClickAsync(); // next month
        await page.Locator("button.mud-picker-calendar-day").Filter(new() { HasText = "15" }).First.ClickAsync();

        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");
        await Expect(result).Not.ToBeVisibleAsync();
    }
}
