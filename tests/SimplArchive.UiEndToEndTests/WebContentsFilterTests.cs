using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The contents list's column filters + vertical scroll (the Tasks tab's pattern applied to the middle pane).
//
// The filter halves mirror WebTasksFilterTests; the scroll half pins what "the list can always be scrolled"
// means in CSS terms — the PANE scrolls (overflow on .wb-list) while the header/filter wrapper stays sticky,
// so a folder taller than the pane keeps both its far rows reachable and its filters on screen.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebContentsFilterTests
{
    private readonly SelfHostedAppFixture _app;

    public WebContentsFilterTests(SelfHostedAppFixture app) => _app = app;

    // Rows addressed by their EXACT name cell, not Filter(HasText): that is a substring match over the whole
    // row, so in a full leg an earlier test's leftover folder whose name merely CONTAINS "2026" wins .First
    // and the test navigates into the wrong place — green alone, red in the leg, exactly the shape the
    // GetByRole-substring lesson warns about.
    private static ILocator Row(ILocator list, string name) =>
        list.Locator(".wb-list-row").Filter(new() { Has = list.Page.Locator(".wb-cname", new() { HasTextRegex = new System.Text.RegularExpressions.Regex($"^\\s*{System.Text.RegularExpressions.Regex.Escape(name)}\\s*$") }) });

    [Fact]
    public async Task Column_filters_narrow_the_rows_and_reset_on_folder_change()
    {
        var page = await Ui.LoginAsync(_app);

        // Business Years / 2026 holds the twelve month folders — enough rows to filter meaningfully.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await Row(list, "Business Years").First.DblClickAsync();
        await Row(list, "2026").First.DblClickAsync();
        await Expect(list.Locator(".wb-list-row")).ToHaveCountAsync(12);

        // Name filter: type into the first filter cell; rows narrow as you type (Immediate, like My Tasks).
        var nameFilter = list.Locator(".wb-cfilters input").First;
        await nameFilter.FillAsync("March");
        await Expect(list.Locator(".wb-list-row")).ToHaveCountAsync(1);
        await Expect(list.Locator(".wb-list-row").First).ToContainTextAsync("03 March");

        // A filter is per-folder view state: navigating to another folder clears it rather than silently
        // hiding the new folder's rows behind last folder's filter.
        await list.Locator(".wb-list-row").First.DblClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.Locator(".wb-cfilters input").First).ToHaveValueAsync(string.Empty);
    }

    [Fact]
    public async Task A_folder_taller_than_the_pane_scrolls_with_the_filters_kept_on_screen()
    {
        // A short viewport, so twelve rows genuinely overflow the pane.
        var page = await Ui.LoginAsync(_app, configureContext: o => o.ViewportSize = new ViewportSize { Width = 1280, Height = 460 });

        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await Row(list, "Business Years").First.DblClickAsync();
        await Row(list, "2026").First.DblClickAsync();
        await Expect(list.Locator(".wb-list-row")).ToHaveCountAsync(12);

        // The pane is the scroller: it must actually overflow, and scrolling it must move content — a pane
        // that grows past its flex row instead would report scrollHeight == clientHeight here and push the
        // bottom tab bar off screen (the WebTabBarTests family's symptom).
        var metrics = await list.EvaluateAsync<int[]>(
            "el => { el.scrollTop = 10000; return [el.scrollHeight, el.clientHeight, el.scrollTop]; }");
        Assert.True(metrics[0] > metrics[1] + 10, $"the pane does not overflow (scrollHeight={metrics[0]}, clientHeight={metrics[1]})");
        Assert.True(metrics[2] > 0, "the pane did not scroll");

        // …and the sticky wrapper keeps the filter row visible at the scrolled position.
        var headTop = await list.Locator(".wb-cheadwrap").EvaluateAsync<double>(
            "el => el.getBoundingClientRect().top - el.closest('[data-pane=list]').getBoundingClientRect().top");
        Assert.True(headTop >= -1 && headTop < 5, $"the header wrapper is not sticky (offset {headTop})");
    }
}
