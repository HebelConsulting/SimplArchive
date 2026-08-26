using Microsoft.Playwright;

namespace SimplArchive.LoadTest;

/// <summary>
/// One simulated person, in a real browser, doing what a person does (#705).
/// </summary>
/// <remarks>
/// <para>
/// A REAL browser rather than an HTTP script, deliberately: what question (b) asks is whether ten people can
/// work reliably, and the honest answer comes from the thing they actually use — a Blazor WASM client whose
/// cost is largely rendering and round-trips the server never sees as one request. An HTTP-level answer would
/// be an inference about user experience rather than an observation of it.
/// </para>
/// <para>
/// <b>Every step is timed and none is asserted.</b> A step that is slow is the measurement; a step that fails
/// is recorded and the loop carries on to the next iteration, because a harness that stops at the first error
/// cannot measure recovery.
/// </para>
/// </remarks>
public sealed class BrowserUser(
    IBrowserContext context, string baseUrl, ActionLog log, string email, string password, int user,
    Pacing pacing)
{
    private IPage? _page;
    private readonly Random _think = new(user * 7919);

    /// <summary>
    /// How long a simulated person looks at what they just opened, before doing the next thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes the run a load test rather than a denial of service.</b> The first kiosk run paused
    /// 2 s per ITERATION, so ten users completed a full browse-open-search cycle every ~3.6 s and generated
    /// <b>~12,000 requests a minute</b> — roughly twenty times what ten real people produce. It did find a real
    /// defect, but it found it by hammering, and a harness that can only report "the server falls over when
    /// nobody would ever push it this hard" answers a question nobody asked.
    /// </para>
    /// <para>
    /// The pause is therefore per ACTION and randomised per user, so the users also stop moving in lockstep —
    /// ten browsers stepping together measure a repeated spike, not ten people. Randomised from a seed derived
    /// from the user index, so a run is still reproducible.
    /// </para>
    /// </remarks>
    private async Task ThinkAsync(CancellationToken cancellationToken) =>
        await Task.Delay(pacing.Next(_think), cancellationToken);

    /// <summary>
    /// Wide, on purpose: this must survive the degradation it exists to measure.
    /// </summary>
    /// <remarks>
    /// The E2E suite tunes these for CI, where a slow action means a broken build. Here a slow action means a
    /// finding — inheriting the suite's ceilings would turn "the server took 40 s" into a harness crash and
    /// lose the data point. Playwright budgets ACTIONS and ASSERTIONS separately, so both are set; that split
    /// has already cost this repository one CI-only mystery.
    /// </remarks>
    private const int TimeoutMs = 120_000;

    /// <summary>Signs in. Its duration is the first thing a degrading server makes visible.</summary>
    public async Task<bool> LoginAsync()
    {
        var sample = await log.TimeAsync("login", user, async () =>
        {
            // The desktop-client promo modal would sit over everything on a fresh profile, and every later step
            // would time out against its overlay — measuring a modal rather than a server.
            await context.AddInitScriptAsync(
                "try { localStorage.setItem('sa.desktopClientNoticeDismissed', '1'); } catch (e) { }");

            _page = await context.NewPageAsync();
            _page.SetDefaultTimeout(TimeoutMs);

            await _page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await _page.GetByText("SimplArchive").First.WaitForAsync();
            await _page.GetByText(new System.Text.RegularExpressions.Regex("^log ?in$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .First.ClickAsync();

            await _page.WaitForSelectorAsync("input[name='Email'], input[type='email']");
            await _page.FillAsync("input[name='Email'], input[type='email']", email);
            await _page.FillAsync("input[name='Password'], input[type='password']", password);
            await _page.ClickAsync("button[type='submit'], input[type='submit']");

            // The workbench, not merely a redirect: the app bar carries the signed-in user.
            await _page.Locator(".wb-appbar").WaitForAsync();
        });

        return !sample.Failed;
    }

    /// <summary>
    /// One pass of the mixed workload.
    /// </summary>
    /// <remarks>
    /// The mix is browse-heavy by design — reading is what most people do most of the time, and a loop weighted
    /// towards writes would report a server under a load nobody applies. Weights were left to the implementer;
    /// this is one browse-and-search pass with an upload every fourth iteration, which keeps writes present
    /// without letting the archive grow faster than a night window can absorb.
    /// </remarks>
    public async Task IterateAsync(int iteration, CancellationToken cancellationToken)
    {
        if (_page is not { } page)
        {
            return;
        }

        await log.TimeAsync("open repository", user, async () =>
        {
            await page.Locator(".wb-tab[aria-label='Repositories']").First.ClickAsync();
            await page.GetByText("Demo Repository").First.ClickAsync();
            await page.Locator("[data-pane='list'] .wb-list-row").First.WaitForAsync();
        });

        await ThinkAsync(cancellationToken);

        await log.TimeAsync("open document", user, async () =>
        {
            // A row with a version, so the preview pane genuinely renders something — clicking a folder would
            // time a cheaper action and flatter the result.
            var name = ((await page.Locator("[data-pane='list'] .wb-list-row").First.InnerTextAsync()) ?? string.Empty)
                .Split('\n')[0]
                .Trim();
            if (name.Length == 0)
            {
                // Refuse to time a vacuous wait. HasText = "" matches everything, so an empty name would
                // reintroduce exactly the defect below while looking like a measurement.
                throw new InvalidOperationException("the first list row had no name to wait for");
            }

            // ADDRESSED BY NAME, NOT BY POSITION — and that is the whole fix (#754).
            //
            // Reading `.First`, then clicking `.First`, is two resolutions of a moving target. Ten users upload
            // concurrently and the workbench re-renders on the SignalR notification, so the row that was first
            // when the name was read need not be first when the click lands. The click then opens a DIFFERENT
            // document, the pane correctly shows that document, and the wait for the old name can never be
            // satisfied — 120 s, every time, with the server having done nothing wrong.
            //
            // This is ADR 0559's rule ("an action is addressed from the row, never from the pane's loaded
            // state") applied to the harness that measures the client: re-resolving by name means a re-render
            // finds the same document again, and a document that genuinely vanished fails FAST on the click
            // rather than hanging on a wait that has become unsatisfiable.
            var row = page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name }).First;

            // The pane CARRYING THIS DOCUMENT'S NAME, not merely the pane. `[data-pane='index']` is part of the
            // workbench layout and exists before the click, so waiting for it was a condition ALREADY TRUE —
            // this row timed a click and nothing else, reporting 0.04 s against a REMOTE host, which is what
            // gave it away. A wait satisfiable by the previous state measures nothing and reads as a fast action.
            // TWO try blocks, not one, because a single one MISATTRIBUTES its own failure. Wrapping both in
            // one block produced "waited … for the index pane to show 'Business Years', but it shows 'Demo
            // Repository'" — which reads as the pane failing to update, and sent the investigation at the
            // workbench. The pane was fine: clicking that folder updates it immediately, verified by hand in a
            // real browser. It was the CLICK that never completed, so the pane was still showing the previous
            // selection exactly as it should. A message that names the wrong step is worse than none.
            try
            {
                // BOUNDED, and much shorter than the default. Playwright will not click an element until it is
                // STABLE — the same bounding box across two animation frames — and during a run the workbench
                // re-renders on every SignalR notification from the other nine users' uploads, so a row can
                // simply never hold still. A person clicks a moving row without noticing; Playwright waits for
                // it to stop. Left at the default that wait costs 120 s per occurrence, which is most of what
                // the old runs spent their time on: eight timeouts per user filled the window and starved the
                // measurement of the iterations it was there to take.
                await row.ClickAsync(new() { Timeout = ClickTimeoutMs });
            }
            catch (TimeoutException timeout)
            {
                throw new InvalidOperationException(
                    $"open document: could not click the row '{name}' within {ClickTimeoutMs} ms "
                    + $"({timeout.Message.Split('\n')[0]}) — the row is present but never held still long enough "
                    + $"for a click. First row is now '{await DescribeFirstRowAsync(page)}'.");
            }

            try
            {
                await page.Locator("[data-pane='index']").Filter(new() { HasText = name }).First.WaitForAsync();
            }
            // System.TimeoutException — VERIFIED, not reasoned. Microsoft.Playwright defines no TimeoutException
            // of its own, from which it is tempting to conclude that the name binds to a type Playwright never
            // throws and the catch is dead. That conclusion is WRONG: Playwright .NET throws
            // System.TimeoutException for waits and clicks alike (probed against a real browser — a missing
            // locator, a click on it, and a Filter that matches nothing all produced System.TimeoutException).
            // The tempting reasoning was followed once, "fixed" to PlaywrightException, and silently disabled
            // the whole block for a full 18-minute run.
            catch (TimeoutException timeout)
            {
                // SAY WHAT WAS EXPECTED AND WHAT IS THERE. A bare "Timeout 120000ms exceeded" cost two runs and
                // three wrong hypotheses — server saturation, a stalled object-storage transfer, generator
                // contention — none of which the message could distinguish, because it named neither the
                // document nor the pane. The report keeps only the first line of this message, so everything
                // that matters has to be on it.
                throw new InvalidOperationException(
                    $"open document: the row '{name}' WAS clicked, but the index pane never showed it "
                    + $"({timeout.Message.Split('\n')[0]}); it shows '{await DescribePaneAsync(page)}' "
                    + $"(first row is now '{await DescribeFirstRowAsync(page)}')");
            }
        });

        await ThinkAsync(cancellationToken);

        await log.TimeAsync("search", user, async () =>
        {
            await page.Locator(".wb-tab[aria-label='Search']").First.ClickAsync();
            var field = page.Locator("[data-tour='search-field'] input, .wb-search input").First;
            await field.FillAsync(SearchTerms[iteration % SearchTerms.Length]);
            await field.PressAsync("Enter");
            await page.WaitForTimeoutAsync(250); // let the request start; the wait below is what is timed
            await page.Locator(".wb-search-results, [data-pane='list']").First.WaitForAsync();
        });

        if (iteration % 4 == 3)
        {
            await ThinkAsync(cancellationToken);

            await log.TimeAsync("upload", user, async () =>
            {
                await page.Locator(".wb-tab[aria-label='Repositories']").First.ClickAsync();
                await page.GetByText("Demo Repository").First.ClickAsync();

                var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
                    await page.Locator(".wb-ribbon [aria-label='Upload']").First.ClickAsync());

                // The STEM is what identifies the row: the client names an uploaded document with
                // Path.GetFileNameWithoutExtension (Home.razor), so the list shows "load-<guid>" and never
                // "load-<guid>.txt". Waiting for the full filename matched nothing and timed out at 120 s —
                // the GOOD failure mode, and the mirror image of the bug this replaced: a wait that is too
                // strict screams, a wait that is already satisfied reports a flatteringly fast action.
                var stem = $"load-{Guid.NewGuid():N}";
                var fileName = $"{stem}.txt";
                await chooser.SetFilesAsync(new FilePayload
                {
                    Name = fileName,
                    MimeType = "text/plain",
                    // Small on purpose: this measures the round trip — presign, PUT, finalize, classify,
                    // re-list — not the network's throughput, and a night window should not leave a gigabyte
                    // behind for the 04:00 reset to clear.
                    Buffer = System.Text.Encoding.UTF8.GetBytes($"load test {DateTimeOffset.UtcNow:O}"),
                });

                // THIS file's row. Waiting for `.wb-list-row` waited for a list that was already rendered by the
                // navigation two lines above — so the upload reported 0.13 s against a remote host, for a round
                // trip it never waited on. The GUID stem cannot match anything that existed before the upload,
                // which is the property a wait needs: satisfiable only by the new state.
                await page.Locator("[data-pane='list'] .wb-list-row")
                    .Filter(new() { HasText = stem }).First.WaitForAsync();
            });
        }

        await ThinkAsync(cancellationToken);
    }

    // Long enough that a merely-busy page still gets clicked, short enough that a row which never stabilises
    // costs one iteration rather than an eighth of the run.
    private const int ClickTimeoutMs = 15000;

    private static readonly string[] SearchTerms = ["invoice", "contract", "report", "demo", "concert"];

    /// <summary>What the index pane actually shows, for a failure message. Never throws.</summary>
    /// <remarks>
    /// Deliberately short-timeout and swallowing: this runs only when the action has ALREADY failed, and a
    /// diagnostic that can itself hang would replace one 120 s mystery with two.
    /// </remarks>
    private static Task<string> DescribePaneAsync(IPage page) => DescribeAsync(page, "[data-pane='index']");

    private static Task<string> DescribeFirstRowAsync(IPage page) => DescribeAsync(page, "[data-pane='list'] .wb-list-row");

    private static async Task<string> DescribeAsync(IPage page, string selector)
    {
        try
        {
            var text = await page.Locator(selector).First.InnerTextAsync(new() { Timeout = 2000 }) ?? string.Empty;
            var firstLine = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(empty)";
            return firstLine.Length > 80 ? $"{firstLine[..80]}…" : firstLine;
        }
        catch (Exception e)
        {
            return $"(unreadable: {e.GetType().Name})";
        }
    }
}
