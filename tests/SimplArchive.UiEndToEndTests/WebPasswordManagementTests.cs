using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "User password management"): the self-service "Change password" dialog opens from the
// corner account menu, and an admin resets a (throwaway) user's password in the Users & groups tab, which
// shows the generated password once. The admin's own password is never changed (the change dialog is only
// opened + cancelled), so the fixture login stays intact.
[Collection(UiCollection.Name)]
public class WebPasswordManagementTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPasswordManagementTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Change_password_dialog_opens_and_admin_can_reset_a_user()
    {
        var page = await Ui.LoginAsync(_app);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = "PW User " + suffix;

        // Self-service: corner avatar → account menu → Change password → the dialog opens (then cancel, so
        // the demo admin's password is untouched).
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Change password…").ClickAsync();
        var changeDialog = page.Locator(".mud-dialog");
        await Expect(changeDialog.Locator("input[type=password]").First).ToBeVisibleAsync();
        await changeDialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        // Admin reset: create a throwaway user, select it, Reset password → the generated password shows once.
        await page.Locator(".wb-tab").Filter(new() { HasText = "Users & groups" }).First.ClickAsync();
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).First.ClickAsync(); // New menu
        await page.GetByText("New user").ClickAsync();
        var newUser = page.Locator(".mud-dialog");
        await newUser.Locator("input").Nth(0).FillAsync($"pw-{suffix}@example.test");
        await newUser.Locator("input").Nth(1).FillAsync(user);
        await newUser.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await Expect(page.GetByText("User created.")).ToBeVisibleAsync();

        await page.Locator(".wb-ug-rows").GetByText(user, new() { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Reset password…" }).ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();

        await Expect(page.GetByText("won't be shown again")).ToBeVisibleAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
    }
}
