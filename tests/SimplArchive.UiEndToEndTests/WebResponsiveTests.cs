using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Responsive web workbench (ADR "Responsive web workbench", iPad-first): below 1200px the lower-priority panes
// collapse so the workbench fits an iPad/phone without horizontal overflow. Verifies the tablet + phone tiers.
[Collection(UiCollection.Name)]
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

    private static async Task<bool> OverflowsHorizontally(IPage page) =>
        await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 2");
}
