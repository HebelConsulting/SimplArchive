using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A row clicked while the folder's contents are reloading must stay selected (#811).
//
// The list deliberately keeps its stale rows clickable while a reload is in flight, and the reload's
// completion re-points a mid-flight selection at the freshly loaded row — but that survive-the-load rule
// (cee29043) was written for document rows only. A FOLDER row clicked inside the window was silently
// reverted to the parent: the detail pane snapped back to the folder the user is standing in, and the click
// was erased with no error. The window is one network round trip, so localhost never sees it — the first
// aggressive load test against the kiosk did, 80 times out of 400 (20 %).
//
// The test forces the window a WAN cannot be relied on to provide: it holds every children answer on a
// delayed route, re-opens the repository so its reload hangs, clicks the folder row against the stale list,
// and then asserts PAST the delayed response landing — the first look at the pane passes even on the broken
// code (the click's own detail load wins the first glance); it is the reload's completion that clobbers.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebSelectionDuringLoadTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSelectionDuringLoadTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_folder_row_clicked_while_the_listing_reloads_stays_selected()
    {
        var page = await Ui.LoginAsync(_app);

        // First visit, unthrottled: the race needs the rows already on screen when the delayed reload starts.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var folderRow = list.Locator(".wb-list-row").Filter(new() { HasText = "Business Years" }).First;
        await Expect(folderRow).ToBeVisibleAsync();

        var delayed = 0;
        await page.RouteAsync("**/children*", async route =>
        {
            delayed++;
            await Task.Delay(1500);
            await route.ContinueAsync();
        });

        // Re-open the same repository — the tree re-fires the load for the node it is already standing on
        // (the load-test harness leans on the same behaviour) — and click the folder row while that load is
        // held by the route.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await folderRow.ClickAsync();

        var head = page.Locator(".wb-detail-head");
        await Expect(head).ToContainTextAsync("Business Years");

        // The actual regression check: outlive the delayed response. On the broken code the reload's
        // completion reverts the pane to 'Demo Repository' about 1.5 s after the re-open.
        await page.WaitForTimeoutAsync(2500);
        await Expect(head).ToContainTextAsync("Business Years");

        Assert.True(delayed > 0, "the children listing was never delayed, so no reload was in flight and this proved nothing");
    }
}
