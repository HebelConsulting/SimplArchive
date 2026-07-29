using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Responsive web workbench (ADR "Responsive web workbench", iPad-first): below 1200px the lower-priority panes
// collapse so the workbench fits an iPad/phone without horizontal overflow. Verifies the tablet + phone tiers.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebResponsiveTests
{
    private readonly SelfHostedAppFixture _app;

    public WebResponsiveTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Workbench_adapts_to_tablet_and_phone_viewports()
    {
        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync(); // render the workbench panes

        // --- iPad portrait ---
        await page.SetViewportSizeAsync(834, 1112);
        await page.WaitForTimeoutAsync(400); // let the resize debounce + reflow settle

        // The chat pane is dropped; the index (mask/detail) pane stays on a tablet.
        await Expect(page.Locator(".wb-chat")).ToBeHiddenAsync();
        await Expect(page.Locator("[data-pane='index']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-pane='tree']")).ToBeVisibleAsync();
        Assert.False(await OverflowsHorizontally(page), "workbench overflows horizontally at tablet width");

        // --- phone ---
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForTimeoutAsync(400);

        // The index pane is also dropped on a phone; tree + list + preview remain.
        await Expect(page.Locator("[data-pane='index']")).ToBeHiddenAsync();
        await Expect(page.Locator("[data-pane='tree']")).ToBeVisibleAsync();
        Assert.False(await OverflowsHorizontally(page), "workbench overflows horizontally at phone width");
    }

    // Pane-adaptive contents columns (ADR "Pane-adaptive contents columns"): when the list pane is too narrow to
    // fit all 5 columns, the pane-width container query drops the lower-priority ones down to Name + the ⋮ menu,
    // which is pinned so it can never be clipped/scrolled off the pane's right edge, and every cell carries a
    // title tooltip so a truncated value stays readable on hover.
    [Fact]
    public async Task List_row_menu_stays_visible_and_cells_carry_tooltips_when_the_pane_is_narrow()
    {
        var page = await Ui.LoginAsync(_app);

        // A narrow list pane (the tablet tier caps it at ~283px here) can't fit the 5-column grid.
        await page.SetViewportSizeAsync(834, 1112);
        await page.WaitForTimeoutAsync(400);
        await page.GetByText("Demo Repository").First.ClickAsync();

        var list = page.Locator("[data-pane='list']");
        var row = list.Locator(".wb-list-row.wb-cols").First;
        await Expect(row).ToBeVisibleAsync();

        // The pane collapsed to Name + ⋮: the Size column header (4th) is dropped.
        await Expect(list.Locator(".wb-chead > span").Nth(3)).ToBeHiddenAsync();

        // The row's ⋮ menu must stay within the pane (not clipped past its right edge) and remain usable.
        var menuBtn = row.Locator("button.mud-icon-button").Last;
        await Expect(menuBtn).ToBeVisibleAsync();
        var paneBox = await list.BoundingBoxAsync();
        var btnBox = await menuBtn.BoundingBoxAsync();
        Assert.NotNull(paneBox);
        Assert.NotNull(btnBox);
        Assert.True(btnBox!.X + btnBox.Width <= paneBox!.X + paneBox.Width + 2,
            "the row ⋮ menu is clipped past the list pane's right edge");
        await menuBtn.ClickAsync();
        await Expect(page.Locator(".mud-popover-open").First).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");

        // The Name cell carries a title tooltip carrying the full value (readable when the column is truncated).
        var title = await row.Locator(".wb-cname").GetAttributeAsync("title");
        Assert.False(string.IsNullOrWhiteSpace(title), "the Name cell has no title tooltip");
    }

    private static async Task<bool> OverflowsHorizontally(IPage page) =>
        await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 2");
}
