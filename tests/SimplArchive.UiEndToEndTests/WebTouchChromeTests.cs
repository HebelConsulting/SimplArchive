using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Touch-emulation guard (issue #360, guarding #350): on a touch-emulated phone context, both the top app bar
// (holding the avatar/account menu) and the bottom workbench tab bar must stay fully within the viewport — a
// bounding-box check, since Playwright's IsVisible can't detect a bar pushed off-screen by overflow. Runs at two
// phone sizes and a portrait tablet. This is the first UI test with HasTouch=true (the rest are viewport-only).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebTouchChromeTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTouchChromeTests(SelfHostedAppFixture app) => _app = app;

    [Theory]
    [InlineData(390, 844)]  // iPhone 12/13/14
    [InlineData(360, 740)]  // small Android
    [InlineData(768, 1024)] // portrait tablet
    public async Task App_bar_and_tab_bar_stay_within_the_viewport_on_touch(int width, int height)
    {
        // Log in touch-enabled but at the default desktop viewport (the login helper waits for the display name,
        // which the responsive app bar hides ≤1199px), then resize to the phone/tablet size — same pattern as
        // WebResponsiveTests. HasTouch is a context-level flag, so it survives the resize.
        var page = await Ui.LoginAsync(_app, configureContext: o => o.HasTouch = true);
        await page.SetViewportSizeAsync(width, height);
        await page.WaitForTimeoutAsync(300); // the wbLayout.js resize hook is debounced (~150ms)

        // The top app bar with the account/avatar box is on screen (top edge visible, bottom within the viewport).
        var appBar = page.Locator(".wb-appbar");
        await Expect(appBar).ToBeVisibleAsync();
        var appBarBox = await appBar.BoundingBoxAsync();
        Assert.NotNull(appBarBox);
        Assert.True(appBarBox!.Y >= -1, $"app bar top {appBarBox.Y} is above the viewport");
        Assert.True(appBarBox.Y + appBarBox.Height <= height + 1, $"app bar bottom {appBarBox.Y + appBarBox.Height} exceeds viewport height {height}");
        // The avatar/account box isn't pushed off the right edge (the original #350 symptom).
        var userBox = page.Locator(".wb-userbox");
        var userBoxBox = await userBox.BoundingBoxAsync();
        Assert.NotNull(userBoxBox);
        Assert.True(userBoxBox!.X + userBoxBox.Width <= width + 1, $"avatar right edge {userBoxBox.X + userBoxBox.Width} exceeds viewport width {width}");

        // The bottom tab bar is fully within the viewport — not pushed below the fold (the #350 / 100vh symptom).
        var tabs = page.Locator(".wb-tabs");
        await Expect(tabs).ToBeVisibleAsync();
        var tabsBox = await tabs.BoundingBoxAsync();
        Assert.NotNull(tabsBox);
        Assert.True(tabsBox!.Y >= 0, $"tab bar top {tabsBox.Y} is above the viewport");
        Assert.True(tabsBox.Y + tabsBox.Height <= height + 1, $"tab bar bottom {tabsBox.Y + tabsBox.Height} exceeds viewport height {height}");
    }
}
