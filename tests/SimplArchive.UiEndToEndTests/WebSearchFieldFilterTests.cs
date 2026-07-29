using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0264/0257): an index-field filter row narrows the search. The seeded document has the Basic
// Entry mask (a Keywords field) but no Keywords value, so a Keywords filter excludes it — proving the field
// filter is applied. OpenSearch is in the fixture; indexing is async, so the initial search polls.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSearchFieldFilterTests
{
    private const string Doc = "Invoice 2025-001";

    private readonly SelfHostedAppFixture _app;

    public WebSearchFieldFilterTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Index_field_filter_narrows_the_results()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab").Filter(new() { HasText = "Search" }).First.ClickAsync();

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

        // Add an index-field filter (Keywords), give it a value the document doesn't have → it's excluded.
        await page.GetByRole(AriaRole.Button, new() { Name = "Filters" }).ClickAsync();
        await page.GetByText("Add index-field filter").ClickAsync();

        var fieldRow = page.Locator(".wb-filter-row").Filter(new() { HasText = "Index field" });
        await fieldRow.Locator(".mud-input-control").First.ClickAsync();
        await page.Locator(".mud-list-item").Filter(new() { HasText = "Keywords" }).First.ClickAsync();

        var value = fieldRow.Locator("input:not([role='combobox'])").Last;
        await value.FillAsync("zzz-nomatch");
        await value.EvaluateAsync("el => el.blur()");

        await input.FillAsync("Invoice");
        await input.PressAsync("Enter");
        await Expect(result).Not.ToBeVisibleAsync();
    }
}
