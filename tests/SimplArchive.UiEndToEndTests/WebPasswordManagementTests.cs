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

        // Self-service: corner avatar → account menu → Edit profile… → Change password… → the dialog opens
        // (then cancel, so the demo admin's password is untouched). Since #464 the password route is inside
        // the profile dialog rather than being its own menu entry; this is the route a user actually takes.
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Edit profile…").ClickAsync();
        var profileDialog = page.Locator(".mud-dialog");
        await profileDialog.GetByRole(AriaRole.Button, new() { Name = "Change password…" }).ClickAsync();

        // The password dialog stacks ON TOP of the profile dialog, so ".mud-dialog" now matches both — take the
        // last, which is the one on top. (The photo crop is inline precisely so this stacking is the exception
        // rather than the rule; ADR 0561.)
        var changeDialog = page.Locator(".mud-dialog").Last;
        await Expect(changeDialog.Locator("input[type=password]").First).ToBeVisibleAsync();
        await changeDialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await profileDialog.First.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();

        // Admin reset: create a throwaway user, select it, Reset password → the generated password shows once.
        await page.Locator(".wb-tab[aria-label=\"Users & groups\"]").First.ClickAsync();
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
