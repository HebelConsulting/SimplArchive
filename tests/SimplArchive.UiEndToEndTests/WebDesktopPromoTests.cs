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
}
