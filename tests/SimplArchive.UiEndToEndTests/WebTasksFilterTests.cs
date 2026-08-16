using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The Tasks tab's visible filter row (#550): typing a non-matching document text hides the seeded demo
// review, clearing restores it, and the overdue-only switch keeps it (the demo review's deadline is in the
// past by construction). Sorting order is covered at the view-model level (DesktopTasksSortFilterTests) —
// one seeded row cannot prove an ordering.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebTasksFilterTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTasksFilterTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_filter_row_narrows_by_document_and_the_overdue_switch_keeps_the_overdue_row()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Tasks\"]").First.ClickAsync();

        var row = page.Locator(".wb-tasks tr").Filter(new() { HasText = "Invoice 2026-003" }).First;
        await Expect(row).ToBeVisibleAsync();

        // A non-matching document filter hides the row; clearing brings it back.
        var documentFilter = page.Locator(".wb-task-filters input[type=text]").First;
        await documentFilter.FillAsync("no-such-document");
        await Expect(row).Not.ToBeVisibleAsync();
        await documentFilter.FillAsync("");
        await Expect(row).ToBeVisibleAsync();

        // Overdue-only keeps the demo review — its deadline is seeded in the past.
        await page.Locator(".wb-task-filters .mud-checkbox-input, .wb-task-filters input[type=checkbox]").First.ClickAsync();
        await Expect(row).ToBeVisibleAsync();
    }
}
