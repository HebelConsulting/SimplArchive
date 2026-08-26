using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Regression guard for the tab-bar layout bug (ADR 0307): the bottom tab bar must stay pinned to the bottom
// of the workbench on EVERY tab. A tab whose content panel doesn't fill the space (missing `flex:1 1 auto;
// min-height:0`) lets the tab bar ride up / get pushed off-screen. STANDING RULE (see CLAUDE.md): every new
// bottom tab must be added to this [Theory] — the Tasks tab regressed this first, then the Check-out tab.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebTabBarTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTabBarTests(SelfHostedAppFixture app) => _app = app;

    [Theory]
    [InlineData("Repositories")]
    [InlineData("Intray")]
    [InlineData("Check-out")]
    [InlineData("Search")]
    [InlineData("Recycle bin")]
    [InlineData("Tasks")]
    [InlineData("My work")]
    [InlineData("Tenant")]
    [InlineData("Tags")]
    [InlineData("Contacts")]
    [InlineData("Calendar")]
    // Added with the long-list fix: these four were missing, and Retention is where a real user hit it.
    [InlineData("Retention")]
    [InlineData("Legal holds")]
    [InlineData("Audit")]
    [InlineData("Users & groups")]
    public async Task Bottom_tab_bar_is_pinned_to_the_bottom_of_the_workbench(string tab)
    {
        var page = await Ui.LoginAsync(_app);

        // Tabs are icon-only on hover-capable computers (#298) — the label lives in `title`/`aria-label`, not visible
        // text — so identify each tab by its aria-label rather than its (now hidden) text.
        await page.Locator($".wb-tab[aria-label='{tab}']").First.ClickAsync();
        await Expect(page.Locator(".wb-tab-active")).ToHaveAttributeAsync("aria-label", tab);

        var tabs = await page.Locator(".wb-tabs").BoundingBoxAsync();
        var wb = await page.Locator(".wb").BoundingBoxAsync();
        Assert.NotNull(tabs);
        Assert.NotNull(wb);

        // The tab bar's bottom edge coincides with the workbench's bottom edge (pinned) rather than riding up
        // with empty space below it — the Tasks-tab bug produced a large gap here.
        var gap = (wb!.Y + wb.Height) - (tabs!.Y + tabs.Height);
        Assert.True(Math.Abs(gap) < 5, $"tab bar not pinned to the bottom on the {tab} tab (gap {gap:0.#}px).");
    }

    // The SAME assertion with the content FORCED to exceed its room, which is the half the test above cannot do.
    //
    // That one renders whatever the test tenant happens to hold, so with a short list the broken markup looks
    // perfect — five of the tabs that were missing `min-height:0` were already in its [Theory] and passing. The
    // bug appears only once content exceeds the space, which is why it reached a live demo: a Retention list
    // simply grew.
    //
    // Shrinking the VIEWPORT was tried first and is not enough — the fixture's lists are short enough to fit
    // even a 420px-high window, so that version passed with the defect still in place. Injecting a tall spacer
    // into the tab's own scroll container reproduces "more content than room" exactly, on any data, and asks the
    // one question that matters: when the content is too big, does the container shrink, or does it push the
    // workbench — and the tab bar with it — off the screen?
    [Theory]
    [InlineData("Retention")]
    [InlineData("Tasks")]
    [InlineData("Tags")]
    [InlineData("Recycle bin")]
    [InlineData("Tenant")]
    public async Task Bottom_tab_bar_stays_pinned_when_the_content_exceeds_its_room(string tab)
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator($".wb-tab[aria-label='{tab}']").First.ClickAsync();
        await Expect(page.Locator(".wb-tab-active")).ToHaveAttributeAsync("aria-label", tab);

        // The scrolling flex child, found by what it IS rather than by a class name, so this keeps working when
        // a tab is restyled. Returns false when the tab has none, which fails the test rather than passing it
        // quietly — a guard that shrugs when it cannot find its subject is the vacuous kind.
        var injected = await page.EvaluateAsync<bool>(@"() => {
            const el = [...document.querySelectorAll('.wb *')].find(e => {
                const s = getComputedStyle(e);
                return s.overflowY === 'auto' && s.flexGrow === '1' && e.clientHeight > 0;
            });
            if (!el) return false;
            const spacer = document.createElement('div');
            spacer.style.height = '4000px';
            spacer.setAttribute('data-test-spacer', '1');
            el.appendChild(spacer);
            return true;
        }");
        Assert.True(injected, $"no scrolling flex child found on the {tab} tab — the guard could not be applied.");

        var tabs = await page.Locator(".wb-tabs").BoundingBoxAsync();
        var wb = await page.Locator(".wb").BoundingBoxAsync();
        Assert.NotNull(tabs);
        Assert.NotNull(wb);

        var gap = (wb!.Y + wb.Height) - (tabs!.Y + tabs.Height);
        Assert.True(Math.Abs(gap) < 5, $"tab bar not pinned on the {tab} tab under tall content (gap {gap:0.#}px).");

        // Pinned AND on screen. A bar shoved past the viewport still sits at its parent's bottom edge, so the
        // gap check alone would call the actual complaint — "I could not reach another tab" — a pass.
        var viewport = page.ViewportSize!;
        Assert.True(
            tabs.Y + tabs.Height <= viewport.Height + 1,
            $"tab bar is off-screen on the {tab} tab: bottom edge at {tabs.Y + tabs.Height:0.#}px in a "
            + $"{viewport.Height}px viewport, so no other tab can be reached.");
    }
}
