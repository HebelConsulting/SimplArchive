using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI smoke of the in-app notifications bell (ADR "Notifications (in-app, first slice)"): the app-bar bell is
// present and opens its menu. (The populated flow — a real notification, mark-read, unread count — is covered
// deterministically by the E2E NotificationsTests with two principals; the demo admin only ever acts on its
// own tenant, and self-actions don't notify.)
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebNotificationsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebNotificationsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Notification_bell_is_present_and_opens()
    {
        var page = await Ui.LoginAsync(_app);

        var bell = page.GetByTitle("Notifications");
        await Expect(bell).ToBeVisibleAsync();
        await bell.ClickAsync();

        // The menu opens — either empty ("No notifications") or populated ("Mark all read").
        await Expect(page.GetByText("No notifications").Or(page.GetByText("Mark all read"))).ToBeVisibleAsync();
    }
}
