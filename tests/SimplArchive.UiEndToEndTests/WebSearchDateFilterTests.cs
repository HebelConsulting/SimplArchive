using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0264/0258): a document-date range filter narrows the search. Setting "document date from" to a
// future month excludes the seeded document (whose document date is March 2026). OpenSearch is in the fixture;
// indexing is async, so the initial search polls.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSearchDateFilterTests
{
    private const string Doc = "Invoice 2026-003";

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
        // TYPED, not picked (#1056): the picker is Editable now, so a click into the input focuses it for
        // typing rather than opening the calendar — and the typed path is the primary one, so it is the
        // tested one (the same rule the desktop booking test follows).
        await page.GetByRole(AriaRole.Button, new() { Name = "Filters" }).ClickAsync();
        var docDateRow = page.Locator(".wb-filter-row").Filter(new() { HasText = "Document date" });
        var fromDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 15).AddMonths(1);
        await docDateRow.Locator("input[placeholder='from']").FillAsync(fromDate.ToString("yyyy-MM-dd"));
        await docDateRow.Locator("input[placeholder='from']").PressAsync("Enter"); // commit the typed date

        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");
        await Expect(result).Not.ToBeVisibleAsync();
    }
}
