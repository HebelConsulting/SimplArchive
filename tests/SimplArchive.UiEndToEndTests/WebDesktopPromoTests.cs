using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The post-logon desktop-client promo (ADR 0505): a one-time notice modal appears after login on a fresh
// browser, and "Don't show this again" (persisted in localStorage) suppresses it on the next load.
[Collection(UiCollection.Name)]
public class WebDesktopPromoTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDesktopPromoTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Desktop_promo_notice_shows_once_and_dont_show_again_suppresses_it()
    {
        // Opt out of LoginAsync's default pre-dismiss so we exercise the real first-run behaviour.
        var page = await Ui.LoginAsync(_app, dismissDesktopPromo: false);

        // On this fresh context (empty localStorage) the notice fires — its Download button is unique to it.
        var download = page.GetByRole(AriaRole.Button, new() { Name = "Download the desktop app" });
        await Expect(download).ToBeVisibleAsync();

        // Tick "Don't show this again" and dismiss with "Maybe later" → the dismissal persists to localStorage.
        await page.GetByText("Don't show this again").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Maybe later" }).ClickAsync();
        await Expect(download).ToHaveCountAsync(0);

        // Reload the same context: the flag is set, so the notice must not reappear.
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(".wb-appbar").GetByText(SelfHostedAppFixture.AdminDisplayName).WaitForAsync();
        await page.WaitForTimeoutAsync(1000); // let the post-logon promo check run (and stay silent)
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Download the desktop app" })).ToHaveCountAsync(0);
    }

    // #427: the choice must survive the dismissal paths that are NOT a button. A MudDialog closes on a backdrop
    // click without running any of its own handlers, so a promo that wrote the flag on its way out lost the tick
    // entirely — the modal vanished, looking both dismissed and accepted, and returned on the next visit. The
    // test above could not see it, because "Maybe later" is the one exit that did work.
    //
    // Asserts localStorage itself rather than only that the modal stayed away: that pins the KEY NAME down, so a
    // rename on one side of the read/write pair fails here instead of in front of a visitor.
    [Fact]
    public async Task Dont_show_again_survives_dismissing_by_clicking_outside_the_dialog()
    {
        var page = await Ui.LoginAsync(_app, dismissDesktopPromo: false);
        var download = page.GetByRole(AriaRole.Button, new() { Name = "Download the desktop app" });
        await Expect(download).ToBeVisibleAsync();

        await page.GetByText("Don't show this again").ClickAsync();

        // Dismiss by clicking the backdrop, not a button.
        await page.Locator(".mud-overlay").First.ClickAsync(new() { Position = new Position { X = 5, Y = 5 } });
        await Expect(download).ToHaveCountAsync(0);

        var stored = await page.EvaluateAsync<string?>(
            "() => localStorage.getItem('sa.desktopClientNoticeDismissed')");
        Assert.Equal("1", stored);

        // And it really suppresses the notice on the next load, which is what the visitor cares about.
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(".wb-appbar").GetByText(SelfHostedAppFixture.AdminDisplayName).WaitForAsync();
        await page.WaitForTimeoutAsync(1000);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Download the desktop app" })).ToHaveCountAsync(0);
    }

    // The converse, so "persist on tick" cannot degrade into "persist always": unticking clears the flag, and the
    // notice comes back.
    [Fact]
    public async Task Unticking_clears_the_stored_choice()
    {
        var page = await Ui.LoginAsync(_app, dismissDesktopPromo: false);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Download the desktop app" })).ToBeVisibleAsync();

        var checkbox = page.GetByText("Don't show this again");
        await checkbox.ClickAsync();
        Assert.Equal("1", await page.EvaluateAsync<string?>(
            "() => localStorage.getItem('sa.desktopClientNoticeDismissed')"));

        await checkbox.ClickAsync();
        Assert.Null(await page.EvaluateAsync<string?>(
            "() => localStorage.getItem('sa.desktopClientNoticeDismissed')"));
    }
}
