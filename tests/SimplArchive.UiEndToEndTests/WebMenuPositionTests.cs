using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Regression guards for the app-bar menu positioning bug. Both the corner account menu and the notification
// bell menu use a hidden (display:none) activator and are opened programmatically from the avatar/bell click,
// so without PositionAtCursor MudBlazor had no anchor box and dropped the popover at the window origin
// (top-left) instead of under its trigger on the right. These assert each opened menu lands in the right half
// of the window (where its trigger is) and stays fully within the viewport horizontally.
[Collection(UiCollection.Name)]
public class WebMenuPositionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebMenuPositionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Account_menu_opens_under_the_corner_avatar_on_the_right()
    {
        var page = await Ui.LoginAsync(_app);
        var width = await page.EvaluateAsync<int>("() => window.innerWidth");

        await page.Locator(".wb-userbox").ClickAsync();
        var item = page.GetByText("Log out").First;
        await Expect(item).ToBeVisibleAsync();

        await AssertInRightHalfAndOnScreen(item, width);
    }

    [Fact]
    public async Task Notification_menu_opens_under_the_bell_on_the_right()
    {
        var page = await Ui.LoginAsync(_app);
        var width = await page.EvaluateAsync<int>("() => window.innerWidth");

        await page.Locator("button[title='Notifications']").ClickAsync();
        // The menu shows either notifications ("Mark all read") or the empty placeholder.
        var item = page.GetByText(new Regex("Mark all read|No notifications")).First;
        await Expect(item).ToBeVisibleAsync();

        await AssertInRightHalfAndOnScreen(item, width);
    }

    private static async Task AssertInRightHalfAndOnScreen(ILocator item, int width)
    {
        var box = await item.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.X > width / 2.0, $"menu opened at x={box.X}; expected the right half (window width={width})");
        Assert.True(box.X >= 0 && box.X + box.Width <= width, $"menu overflows the viewport: x={box.X}, width={box.Width}, window={width}");
    }
}
