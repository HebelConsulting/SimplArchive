using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Does a DOCUMENT row clicked while its folder's listing reloads still offer Download? (#816)
//
// #816 records WebDownloadTests.Downloads_the_selected_document failing twice under load — once locally under
// concurrent legs, once in CI on an IMAP-only diff that cannot have caused it — each time because
// `.wb-ribbon [aria-label="Download"]` never left its disabled state. The issue's hypothesis is the ADR 0559
// family: Download enables from `_downloadUrl`, which only a COMPLETED detail load sets, and a load that
// returns `Nothing` leaves it null with nothing scheduled to set it later. If a click inside the reload
// window can produce that, the affordance is not slow — it is permanently absent, and the minute-long
// timeout in the CI log is the test waiting for something that was never coming.
//
// Same instrument as WebSelectionDuringLoadTests (#811), which proved the folder-row half of this window:
// hold every children answer on a delayed route, re-open the repository so its reload hangs, click against
// the stale list, and assert PAST the delayed response landing — because it is the reload's COMPLETION that
// re-points the selection, so the first glance passes either way.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebDownloadDuringLoadTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDownloadDuringLoadTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_document_row_clicked_while_the_listing_reloads_still_offers_download()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");

        // First visit, unthrottled: the race needs the document rows already on screen when the delayed
        // reload starts. Same seeded path the plain download test walks.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await list.GetByText("Contracts").First.DblClickAsync();
        await list.GetByText("Acme Corp").First.DblClickAsync();
        var documentRow = list.Locator(".wb-list-row").Filter(new() { HasText = "Invoice 2026-003" }).First;
        await Expect(documentRow).ToBeVisibleAsync();

        var delayed = 0;
        await page.RouteAsync("**/children*", async route =>
        {
            delayed++;
            await Task.Delay(1500);
            await route.ContinueAsync();
        });

        // Re-open the folder the rows belong to, so its reload is in flight and held, then click the document
        // row against the stale-but-clickable list.
        //
        // Scoped to the TREE deliberately: "Acme Corp" is also the breadcrumb and was a list row one level up,
        // and a click landing on either of those selects without re-firing a children load — which would leave
        // nothing in flight and quietly turn this into a test of the ordinary path.
        var beforeReopen = delayed;
        await page.Locator("[data-pane='tree']").GetByText("Acme Corp").First.ClickAsync();
        await documentRow.ClickAsync();

        // Outlive the delayed response: the reload's completion is what re-points the selection at a freshly
        // parsed row and runs a second detail load. Download must survive that, not merely precede it.
        await page.WaitForTimeoutAsync(2500);

        var download = page.Locator(".wb-ribbon [aria-label=\"Download\"]").First;
        await Expect(download).ToBeEnabledAsync();

        // Not `delayed > 0`: some children answer being delayed says nothing about WHICH one. What this test
        // needs is that the RE-OPEN itself was held, because that is the load whose completion re-points the
        // selection — the event under test.
        Assert.True(delayed > beforeReopen,
            $"re-opening the folder fired no children load ({beforeReopen} delayed before, {delayed} after), "
            + "so nothing was in flight when the row was clicked and this proved nothing");
    }
}
