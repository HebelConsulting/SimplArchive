using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of #465: a user's e-mail — their login identifier — shown in the Users & groups detail pane and
// editable inline. The desktop defines the behaviour (ADR 0511) and this is the web following it: text with a
// pencil, ✓/✕ in the pencil's own row (ADR 0550), Esc to cancel, and a 409 surfaced as a message rather than
// a silent no-op.
//
// The pencil is bound to the row's `email` rel, which is what makes the kiosk case work without the client
// knowing anything about deployments — so the assertion that matters most here is that the address the PUT
// goes to came from the listing.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebUserEmailTests
{
    private readonly SelfHostedAppFixture _app;

    public WebUserEmailTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task An_administrator_edits_a_users_address_and_a_collision_is_refused()
    {
        var page = await Ui.LoginAsync(_app);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"mail-user-{suffix}";

        // The address already in use is the DEMO ADMIN's own — one create instead of two, and the collision it
        // proves is the same one (the tenant-scoped unique index on the normalized column).
        var taken = SelfHostedAppFixture.AdminEmail;

        await OpenTabAsync(page);
        await CreateUserAsync(page, name, $"mail-before-{suffix}@example.test");

        var rows = page.Locator(".wb-ug-rows");
        await rows.GetByText(name).ClickAsync();

        // The address reads as text until the pencil is used.
        var pane = page.Locator(".wb-ug-email");
        await Expect(pane).ToContainTextAsync($"mail-before-{suffix}@example.test");

        // Cancel discards — the address on screen is still the old one.
        await pane.GetByRole(AriaRole.Button, new() { Name = "Change e-mail address" }).ClickAsync();
        await page.Locator(".wb-ug-email-entry input").FillAsync($"mail-discarded-{suffix}@example.test");
        await pane.GetByRole(AriaRole.Button, new() { Name = "Discard the e-mail change" }).ClickAsync();
        await Expect(pane).ToContainTextAsync($"mail-before-{suffix}@example.test");

        // An address another user already holds is refused, and the pane stays in edit so the typo is fixable.
        await pane.GetByRole(AriaRole.Button, new() { Name = "Change e-mail address" }).ClickAsync();
        await page.Locator(".wb-ug-email-entry input").FillAsync(taken);
        await pane.GetByRole(AriaRole.Button, new() { Name = "Save the e-mail address" }).ClickAsync();
        await Expect(page.GetByText("A user with this email already exists.")).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-ug-email-entry")).ToBeVisibleAsync();

        // A free address commits, and survives leaving the tab and coming back (so it really persisted).
        var after = $"mail-after-{suffix}@example.test";
        await page.Locator(".wb-ug-email-entry input").FillAsync(after);
        await pane.GetByRole(AriaRole.Button, new() { Name = "Save the e-mail address" }).ClickAsync();
        await Expect(page.GetByText("E-mail address changed.")).ToBeVisibleAsync();

        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await OpenTabAsync(page);
        await page.Locator(".wb-ug-rows").GetByText(name).ClickAsync();
        await Expect(page.Locator(".wb-ug-email")).ToContainTextAsync(after);
    }

    // A group has no login address, so the row carries neither the field nor the affordance.
    [Fact]
    public async Task A_group_has_no_address_row()
    {
        var page = await Ui.LoginAsync(_app);
        var group = $"mail-group-{Guid.NewGuid().ToString("N")[..8]}";

        await OpenTabAsync(page);
        await CreateGroupAsync(page, group);
        await page.Locator(".wb-ug-rows").GetByText(group).ClickAsync();

        await Expect(page.Locator(".wb-ug-rights-head")).ToContainTextAsync(group);
        await Expect(page.Locator(".wb-ug-email")).ToHaveCountAsync(0);
    }

    private static async Task OpenTabAsync(IPage page)
    {
        await page.Locator(".wb-tab[aria-label=\"Users & groups\"]").First.ClickAsync();
        await Expect(page.Locator(".wb-ug")).ToBeVisibleAsync();
    }

    private static async Task CreateUserAsync(IPage page, string displayName, string email)
    {
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).First.ClickAsync(); // New menu
        await page.GetByText("New user").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").Nth(0).FillAsync(email);
        await dialog.Locator("input").Nth(1).FillAsync(displayName);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0);
    }

    private static async Task CreateGroupAsync(IPage page, string name)
    {
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).First.ClickAsync(); // New menu
        await page.GetByText("New group").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").First.FillAsync(name);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0);
    }
}
