using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The tablet tier (#684): one pane held upright, two held sideways, and the tree always reachable.
//
// A tablet cannot be identified by WIDTH — an iPad Pro is 1024px in portrait and 1366px in landscape, so it
// would land in the tablet tier one way up and the desktop tier the other. The tier keys on `(pointer: coarse)`
// instead, which is why every test here needs HasTouch: without it the same viewport is just a small desktop
// window, and these assertions would silently be testing the wrong tier.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebTabletTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTabletTests(SelfHostedAppFixture app) => _app = app;

    // Touch is a CONTEXT property and must be set up front; the viewport is resized AFTERWARDS, because
    // LoginAsync's readiness probe waits for the user name in the app bar to be visible and the responsive CSS
    // hides it on a narrow screen — so logging in at tablet size hangs for 60s on an element that is present
    // and hidden. The phone tests resize after login for the same reason.
    private async Task<IPage> TabletAsync(int w, int h)
    {
        var page = await Ui.LoginAsync(_app, configureContext: o => o.HasTouch = true);
        await page.SetViewportSizeAsync(w, h);
        await page.WaitForTimeoutAsync(700); // the debounced resize hook re-reports the tier to Blazor
        return page;
    }

    // The premise every other test here rests on. If touch emulation ever stops implying a coarse pointer, the
    // tier silently stops applying and the rest of this class would assert the DESKTOP layout while claiming to
    // describe a tablet — so the premise is asserted rather than assumed.
    [Fact]
    public async Task Touch_emulation_really_reports_a_coarse_pointer()
    {
        var page = await TabletAsync(1024, 1366);
        Assert.True(await page.EvaluateAsync<bool>("() => matchMedia('(pointer: coarse)').matches"));
    }

    [Fact]
    public async Task Held_upright_it_shows_one_pane_like_a_phone()
    {
        var page = await TabletAsync(1024, 1366); // iPad Pro portrait — wide enough to be a DESKTOP by width
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        // The list is the full-width view and the tree is off-screen as a drawer: the phone shape, reached on a
        // 1024px-wide screen, which is the whole point of not deciding this by width.
        await Expect(list).ToBeVisibleAsync();
        var treeBox = await tree.BoundingBoxAsync();
        Assert.True(treeBox is null || treeBox.X < 0, $"tree should be off-screen as a drawer, was at x={treeBox?.X}");

        // The hamburger brings it back — the same control the phone uses.
        await page.GetByLabel("Folders").First.ClickAsync();
        await page.WaitForTimeoutAsync(500);
        var opened = await tree.BoundingBoxAsync();
        Assert.True(opened is { X: >= 0 }, "the drawer did not open");
    }

    [Fact]
    public async Task Held_sideways_it_shows_the_tree_beside_the_list_while_browsing()
    {
        var page = await TabletAsync(1366, 1024); // iPad Pro landscape
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        await Expect(tree).ToBeVisibleAsync();
        await Expect(list).ToBeVisibleAsync();

        var t = await tree.BoundingBoxAsync();
        var l = await list.BoundingBoxAsync();
        Assert.NotNull(t);
        Assert.NotNull(l);
        // Side by side, not overlaid: the tree is INLINE while browsing, so no drawer is needed to see it.
        Assert.True(t!.X >= 0 && l!.X > t.X, $"expected tree|list side by side, got tree.x={t.X} list.x={l.X}");

        // Two of the three: chat never shows on a tablet, and the detail waits until something is selected.
        await Expect(page.Locator("[data-pane='chat']")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Selecting_a_document_swaps_the_tree_for_the_detail()
    {
        var page = await TabletAsync(1366, 1024);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await Expect(list.Locator(".wb-list-row").First).ToBeVisibleAsync();

        // Drill to a folder holding documents, then select one.
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" }).First.DblClickAsync();
        await page.WaitForTimeoutAsync(1200);
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Acme Corp" }).First.DblClickAsync();
        await page.WaitForTimeoutAsync(1200);
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Offer 2026-014" }).First.ClickAsync();
        await page.WaitForTimeoutAsync(1200);

        // The pair is now list | detail — the parent stays beside the child.
        var detail = page.Locator(".wb-detail");
        await Expect(detail).ToBeVisibleAsync();
        var l = await list.BoundingBoxAsync();
        var d = await detail.BoundingBoxAsync();
        Assert.True(l is not null && d is not null && d.X > l.X, $"expected list|detail side by side, got list.x={l?.X} detail.x={d?.X}");

        // ...and the tree has stepped aside, but is one tap away. This is the "always able to return" promise:
        // asserting the hamburger EXISTS is the point, not that the drawer looks right.
        var treeBox = await page.Locator("[data-pane='tree']").BoundingBoxAsync();
        Assert.True(treeBox is null || treeBox.X < 0, $"tree should have stepped aside, was at x={treeBox?.X}");

        await page.GetByLabel("Folders").First.ClickAsync();
        await page.WaitForTimeoutAsync(500);
        var opened = await page.Locator("[data-pane='tree']").BoundingBoxAsync();
        Assert.True(opened is { X: >= 0 }, "the tree could not be reached from the detail view");
    }

    // The tier must not capture a laptop. Same viewport, no touch — the desktop layout, chat pane and all.
    [Fact]
    public async Task A_mouse_driven_window_of_the_same_size_stays_on_the_desktop_layout()
    {
        var page = await Ui.LoginAsync(_app);
        await page.SetViewportSizeAsync(1366, 1024);
        await page.WaitForTimeoutAsync(700);

        await Expect(page.Locator("[data-pane='tree']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-pane='chat']")).ToBeVisibleAsync();
    }
}
