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
    [InlineData("Inbox")]
    [InlineData("Check-out")]
    [InlineData("Search")]
    [InlineData("Recycle bin")]
    [InlineData("Tasks")]
    [InlineData("My work")]
    [InlineData("Tenant")]
    [InlineData("Tags")]
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
}
