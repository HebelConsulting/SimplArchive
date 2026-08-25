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
[Trait("Area", "ui-4")]
public class WebMenuPositionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebMenuPositionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Account_menu_opens_under_the_corner_avatar_on_the_right()
    {
        var page = await Ui.LoginAsync(_app);
        var width = await page.EvaluateAsync<int>("() => window.innerWidth");

        var trigger = page.Locator(".wb-userbox");
        await trigger.ClickAsync();
        var item = page.GetByText("Log out").First;
        await Expect(item).ToBeVisibleAsync();

        await AssertAnchoredToTriggerAndOnScreen(item, trigger, width);
    }

    [Fact]
    public async Task Notification_menu_opens_under_the_bell_on_the_right()
    {
        var page = await Ui.LoginAsync(_app);
        var width = await page.EvaluateAsync<int>("() => window.innerWidth");

        var trigger = page.Locator("button[title='Notifications']");
        await trigger.ClickAsync();
        // The menu shows either notifications ("Mark all read") or the empty placeholder — and which one decides
        // how WIDE it is, which is precisely why the assertion must not depend on the menu's width.
        var item = page.GetByText(new Regex("Mark all read|No notifications")).First;
        await Expect(item).ToBeVisibleAsync();

        await AssertAnchoredToTriggerAndOnScreen(item, trigger, width);
    }

    // The bug these guard is "the popover ignored its trigger and landed at the window origin". So the assertion
    // is that the menu is ANCHORED TO ITS TRIGGER — the trigger's horizontal centre falls inside the menu's span
    // — and that it stays on screen.
    //
    // NOT "the menu is in the right half", which is what this used to assert. That held only while the menu was
    // narrow: a populated notification menu is ~313px wide, and a wide menu hanging off a right-edge trigger
    // correctly extends LEFTWARDS, so its items sit just left of centre. The old assertion therefore passed on an
    // empty menu and failed on a full one — which is why it failed in long single-process runs (140 tests'
    // notifications accumulate for the shared demo user) while passing in isolation and in CI's four legs, and
    // why it reported the same x=588 every time. It was measuring content, not position (issue #420).
    private static async Task AssertAnchoredToTriggerAndOnScreen(ILocator menuItem, ILocator trigger, int width)
    {
        var box = await menuItem.BoundingBoxAsync();
        var triggerBox = await trigger.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.NotNull(triggerBox);

        // The menu item spans some of the menu's width; the trigger's centre should fall within the menu's
        // horizontal extent (with a tolerance for the popover's own padding/offset).
        var triggerCentre = triggerBox!.X + (triggerBox.Width / 2.0);
        const double tolerance = 40;
        Assert.True(
            triggerCentre >= box!.X - tolerance && triggerCentre <= box.X + box.Width + tolerance,
            $"the menu is not anchored to its trigger: menu spans {box.X}..{box.X + box.Width}, trigger centre {triggerCentre} (window {width})");

        // The original failure mode: dropped at the window origin rather than under the trigger.
        Assert.True(box.X > 0, $"menu opened at the window origin (x={box.X}) instead of under its trigger");

        Assert.True(box.X >= 0 && box.X + box.Width <= width, $"menu overflows the viewport: x={box.X}, width={box.Width}, window={width}");
    }
}
