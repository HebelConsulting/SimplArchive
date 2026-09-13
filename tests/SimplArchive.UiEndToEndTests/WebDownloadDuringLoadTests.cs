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

        // Walked down in the TREE rather than by double-clicking list rows, and that is the whole of this
        // test's history with #420.
        //
        // Opening a folder from the LIST is what used to put its node in the tree, via TreeState.RevealAsync —
        // which searches only the ALREADY-LOADED tree, returns false when the ancestor chain has not arrived
        // yet, and schedules NOTHING to try again. So under load the node is not late, it is never coming, and
        // this test sat waiting for it until the timeout. That is why it failed only in a long single-process
        // run and passed alone: the reveal wins the race on an idle machine.
        //
        // Two earlier explanations were measured and discarded, which is why this comment is long: it is not
        // shared-data (nothing in the suite moves this folder) and it is not the throttle below racing the
        // setup (the failure moved to an assertion placed BEFORE the route was installed). Bisecting found no
        // culprit either — 57 predecessor classes pass and only the full 116 reproduce.
        //
        // Expanding the tree puts the node there BY CONSTRUCTION, so nothing here depends on a one-shot
        // side-effect firing in time. The list still ends up listing this folder, because clicking a tree node
        // opens it — which is also what the re-open below relies on.
        var tree = page.Locator("[data-pane='tree']");
        var repository = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First;
        await Expect(repository).ToBeVisibleAsync();
        await repository.Locator(".mud-treeview-item-arrow").ClickAsync();

        var contracts = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Contracts" }).First;
        await Expect(contracts).ToBeVisibleAsync();
        await contracts.Locator(".mud-treeview-item-arrow").ClickAsync();

        // Scoped to the TREE deliberately: "Acme Corp" is also the breadcrumb and a list row one level up, and
        // a click landing on either of those selects without re-firing a children load — which would leave
        // nothing in flight and quietly turn this into a test of the ordinary path.
        var treeFolder = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Acme Corp" }).First;
        await Expect(treeFolder).ToBeVisibleAsync();
        await treeFolder.ClickAsync();

        // The rows must be on screen before the delayed reload starts — that is the state the race needs.
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
        var beforeReopen = delayed;
        await treeFolder.ClickAsync();
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
