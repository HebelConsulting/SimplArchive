using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A failed call during a detail load must not degrade the pane silently (#816).
//
// The loader makes about eight sequential requests for the selected document. Any one of them failing threw
// to a catch that swallowed it and returned "nothing loaded" — so `_downloadUrl` stayed null with nothing
// scheduled to set it again, and Download, the preview, the workflow transitions and the tags were gone for
// that selection with no error shown. Reselecting was the only way back, and nothing on screen said so.
//
// It lived for a year as a FLAKY TEST: #816 recorded WebDownloadTests failing twice under load, once in CI on
// an IMAP-only diff, each time with the Download button never leaving its disabled state until the timeout
// expired. The hypothesis on the issue was the ADR 0559 click-during-reload window; that was tested
// separately (WebDownloadDuringLoadTests) and does NOT reproduce. This does, on the first try, with no
// contention needed — contention only makes the failing request likely.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebDetailLoadFailureTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDetailLoadFailureTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_failed_call_during_a_detail_load_says_so_and_keeps_download_reachable()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        await list.GetByText("Contracts").First.DblClickAsync();
        await list.GetByText("Acme Corp").First.DblClickAsync();
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = "Invoice 2026-003" }).First;
        await Expect(row).ToBeVisibleAsync();

        // Let the FOLDER's own detail load finish before injecting anything. Opening a folder selects it, and
        // selecting loads its details — so a /versions call for a different document is still in the air while
        // the row below is already visible. Aborting "the first call" then hit the folder's load about 1 run
        // in 5, and DetailLoader correctly DISCARDS a superseded result rather than painting it (ADR 0559),
        // so no message appeared and the test waited 30 s for something that was never going to happen.
        //
        // The product was right every time. The test was racing an overlap it had not waited for.
        await Ui.WaitForDocumentApiQuietAsync(page);

        // Fail exactly ONE call of the detail load, the way a transient blip under contention does — then let
        // every later call through, so what this measures is the client's handling and not a broken server.
        var aborted = 0;
        await page.RouteAsync("**/versions*", async route =>
        {
            if (aborted++ == 0)
            {
                await route.AbortAsync();
                return;
            }

            await route.ContinueAsync();
        });

        await row.ClickAsync();

        // Half one: the user is told. Before the fix the pane degraded in total silence, which is what made
        // this a mystery rather than a bug report.
        // The INJECTION is confirmed FIRST, and the order is the point: asserting the message first leaves this
        // check unreachable exactly when it would explain the failure, so "nothing was injected" and "the
        // client stayed silent" — opposite defects — arrive as one identical 30-second timeout.
        var injected = System.Diagnostics.Stopwatch.StartNew();
        while (aborted == 0 && injected.ElapsedMilliseconds < 15000)
        {
            await Task.Delay(50);
        }

        Assert.True(aborted > 0, "the versions call was never intercepted, so no failure was injected and this proved nothing");

        await Expect(page.GetByText("could not be loaded")).ToBeVisibleAsync();

        // Half two: the affordance is reachable again by the means the message names. Reselecting is a real
        // recovery only if the second load is allowed to succeed — which is why the route aborts once.
        //
        // Esc to deselect (ADR 0703) rather than clicking another row: the folder this stands in holds exactly
        // the one document the test knows by name, and clicking the row it is ALREADY on would not re-fire a
        // load at all — which would make the retry a no-op dressed as a recovery.
        await list.PressAsync("Escape");
        await row.ClickAsync();
        await Expect(page.Locator(".wb-ribbon [aria-label=\"Download\"]").First).ToBeEnabledAsync();
    }
}
