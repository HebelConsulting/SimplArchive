using Microsoft.Playwright;

namespace SimplArchive.UiEndToEndTests;

// The detail pane's vertical economy and its drag-as-peek rule (ADR 0550, issue #413).
//
// Both halves of this shipped unguarded, and both are the kind of rule that degrades **silently**: the pane keeps
// working, it just quietly takes space from the preview — the thing the user opened the document to look at. That
// is exactly the failure mode a behaviour-driven test misses, so these assert MEASUREMENTS: the rendered height,
// whether the pane can scroll, and what reached localStorage.
//
// The bugs these lock down, both found by measuring in a real browser:
//  - `flex: 0 1 auto` let the preview SHRINK this pane below its own content, so a short detail scrolled while the
//    cap still had room (253px of content displayed in 225px). A scrollbar there makes the user scroll to discover
//    there was nothing more to see.
//  - A drag used to rewrite the persisted cap (210 → whatever you dragged to) and that survived a reload, so one
//    drag silently became a permanent preference for every future selection and every future session.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebDetailPaneSizingTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDetailPaneSizingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_short_detail_set_fits_its_content_without_a_scrollbar()
    {
        var page = await Ui.LoginAsync(_app);
        var index = page.Locator("[data-pane='index']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        await index.GetByText("Contents sort order").First.WaitForAsync();

        var m = await MeasureAsync(index);

        // The whole point of the rule: no scrollbar for a short set. Asserting `scrollHeight <= clientHeight`
        // rather than a pixel height keeps this robust to the folder's field list changing.
        Assert.False(m.Overflows, $"the pane scrolls with only a folder's few rows in it ({m.ScrollHeight}px of content in {m.ClientHeight}px)");

        // And it must not be padding itself out to the cap either — that would take rows from the preview for
        // nothing. A folder's detail is a handful of rows, so comfortably under.
        Assert.True(m.Height < 200, $"a folder's detail pane should fit its few rows, not stretch (was {m.Height}px)");
    }

    // This one asserts the MECHANISM, not a rendered outcome, and that is deliberate.
    //
    // The rule is "the preview may never squeeze this pane below its own content". Its visible symptom only
    // appears in a narrow window — the content must be under the 210px cap (or the pane legitimately scrolls
    // anyway) *and* there must be enough layout pressure to shrink it. A folder's few rows on a roomy viewport
    // has neither, which is why the fit-content test above passes against the broken code and cannot guard this;
    // a short viewport did not reliably produce it either. Choosing a document and a window size that happen to
    // land in that window would be a guard that quietly stops guarding the moment either changes.
    //
    // `flex-shrink` IS the rule, stated exactly and without a magic fixture: 0 means the preview cannot take
    // space this pane is using for content; 1 (what shipped) means it can.
    [Fact]
    public async Task The_detail_pane_refuses_to_shrink_below_its_own_content()
    {
        var page = await Ui.LoginAsync(_app);
        var index = page.Locator("[data-pane='index']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        await index.GetByText("Contents sort order").First.WaitForAsync();

        var shrink = await index.EvaluateAsync<string>("el => getComputedStyle(el).flexShrink");
        Assert.Equal("0", shrink);
    }

    [Fact]
    public async Task A_drag_is_a_peek__it_lapses_on_the_next_selection_and_leaves_nothing_behind()
    {
        var page = await Ui.LoginAsync(_app);
        var index = page.Locator("[data-pane='index']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        await index.GetByText("Contents sort order").First.WaitForAsync();
        var fitted = await MeasureAsync(index);

        // A REAL drag through the gutter's own handlers. Setting `style.flex` directly would prove nothing: it
        // bypasses the state the reset keys off, which is how an earlier attempt at this reached a wrong verdict.
        await DragIndexGutterAsync(page, toHeight: 400);

        var peeked = await MeasureAsync(index);
        Assert.True(peeked.Height > fitted.Height + 100, $"the drag did not enlarge the pane ({fitted.Height}px → {peeked.Height}px)");

        // Nothing about a peek may reach storage — that is what stops it outliving the selection it was for.
        Assert.False(await IndexSizePersistedAsync(page), "the drag wrote a height for the detail pane into localStorage");

        // Change the selection: a folder row in the contents list. The peek's whole justification is that the
        // fitted height would move here anyway, so this is where it stops meaning anything.
        await page.Locator("[data-pane='list']").GetByText("Contracts").First.ClickAsync();
        await index.GetByText("Contracts").First.WaitForAsync();

        var afterSelection = await MeasureAsync(index);
        Assert.False(afterSelection.Overflows, "the pane scrolls after the peek lapsed");
        Assert.True(afterSelection.Height < peeked.Height - 100,
            $"the drag did not lapse on the next selection (still {afterSelection.Height}px after a {peeked.Height}px peek)");

        // The cap is a constant, not something a drag may raise: had it been raised, this would be 400px.
        Assert.Equal("210px", afterSelection.MaxHeight);
        Assert.False(await IndexSizePersistedAsync(page), "the drag left a persisted height behind");
    }

    // A plain DTO rather than Dictionary<string, object>: Playwright's deserializer does not surface the object's
    // keys that way, and the resulting KeyNotFoundException looks like a page problem rather than a mapping one.
    private sealed class PaneMetrics
    {
        public int Height { get; set; }
        public int ScrollHeight { get; set; }
        public int ClientHeight { get; set; }
        public string MaxHeight { get; set; } = "";

        public bool Overflows => ScrollHeight > ClientHeight;
    }

    private static Task<PaneMetrics> MeasureAsync(ILocator pane) =>
        pane.EvaluateAsync<PaneMetrics>(
            """
            el => ({
                height: el.offsetHeight,
                scrollHeight: el.scrollHeight,
                clientHeight: el.clientHeight,
                maxHeight: el.style.maxHeight,
            })
            """);

    // Specifically `sizes.index` — the key a drag used to write. Matching on the raw JSON would also hit
    // `collapsed.index`, which is a different, legitimate key.
    private static Task<bool> IndexSizePersistedAsync(IPage page) =>
        page.EvaluateAsync<bool>(
            """
            () => {
                const raw = localStorage.getItem('simplarchive.wb-layout');
                if (!raw) return false;
                try { return 'index' in (JSON.parse(raw).sizes ?? {}); } catch { return false; }
            }
            """);

    // Drives the gutter's own mousedown/mousemove/mouseup listeners, so the module takes the same code path a
    // user's drag does. Playwright's mouse API would work too, but the gutter is a few pixels tall and hitting it
    // by coordinate is flaky; dispatching on the element itself is deterministic and still real events.
    private static async Task DragIndexGutterAsync(IPage page, int toHeight) =>
        await page.EvaluateAsync(
            """
            h => {
                const el = document.querySelector('[data-pane="index"]');
                const g = document.querySelector('[data-gutter="index"]');
                const r = el.getBoundingClientRect(), gr = g.getBoundingClientRect();
                g.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, clientX: gr.left + 5, clientY: gr.top + 2 }));
                document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: gr.left + 5, clientY: r.top + h }));
                document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: gr.left + 5, clientY: r.top + h }));
            }
            """, toHeight);
}
