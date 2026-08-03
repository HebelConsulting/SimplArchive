using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "Retention policies (auto-disposition)"): the demo admin — granted CanManageClassification by
// the demo seed — sees the Retention tab, which lists the demo document (its Basic Entry mask carries a 7-year
// retention) with its computed disposition date. Read-only, so nothing else in the suite is affected.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebRetentionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebRetentionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Retention_tab_lists_the_scheduled_document()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label=\"Retention\"]").First.ClickAsync();
        await Expect(page.Locator(".wb-retention")).ToBeVisibleAsync();

        // The demo document (Basic Entry mask, 7-year retention) is on the schedule.
        var view = page.Locator(".wb-retention");
        await Expect(view.GetByText("Invoice 2025-001").First).ToBeVisibleAsync();
        await Expect(view.GetByText("7 years").First).ToBeVisibleAsync();
        await Expect(view.GetByText("Scheduled").First).ToBeVisibleAsync();
    }
}
