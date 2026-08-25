using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "User impersonation"): the demo admin (who holds CanImpersonate) creates a non-admin user,
// impersonates them from the Users & groups tab (a banner appears + the app acts as that user), then stops
// impersonating and returns to their own session.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebImpersonationTests
{
    private readonly SelfHostedAppFixture _app;

    public WebImpersonationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Impersonate_a_user_then_stop()
    {
        var page = await Ui.LoginAsync(_app);
        var userName = "imp-user-" + Guid.NewGuid().ToString("N")[..8];

        // Users & groups tab → create a user → select it.
        await page.Locator(".wb-tab[aria-label=\"Users & groups\"]").First.ClickAsync();
        await Expect(page.Locator(".wb-ug")).ToBeVisibleAsync();
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).First.ClickAsync();
        await page.GetByText("New user").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").Nth(0).FillAsync($"{userName}@example.test");
        await dialog.Locator("input").Nth(1).FillAsync(userName);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await page.Locator(".wb-ug-rows").GetByText(userName).ClickAsync();

        // Impersonate → the page reloads as the target user and shows the banner.
        await page.GetByRole(AriaRole.Button, new() { Name = "Impersonate", Exact = true }).ClickAsync();
        var banner = page.Locator(".wb-impersonation");
        await Expect(banner).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Expect(banner).ToContainTextAsync(userName);

        // Stop → the banner disappears and the corner shows the admin again.
        await banner.GetByRole(AriaRole.Button, new() { Name = "Stop impersonating" }).ClickAsync();
        await Expect(page.Locator(".wb-impersonation")).Not.ToBeVisibleAsync(new() { Timeout = 30000 });
        await Expect(page.Locator(".wb-appbar").GetByText(SelfHostedAppFixture.AdminDisplayName)).ToBeVisibleAsync();
    }
}
