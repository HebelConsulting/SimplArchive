using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "Group membership editing"): in the Users & groups tab, add a user to a group via the
// searchable member picker — the member appears in the group's Members list — then remove it.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebGroupMembershipTests
{
    private readonly SelfHostedAppFixture _app;

    public WebGroupMembershipTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Add_and_remove_a_group_member_via_the_picker()
    {
        var page = await Ui.LoginAsync(_app);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var group = "mem-grp-" + suffix;
        var user = "Mem User " + suffix;

        await page.Locator(".wb-tab").Filter(new() { HasText = "Users & groups" }).First.ClickAsync();

        // Create the user and the group.
        await CreateUserAsync(page, user, $"mem-{suffix}@example.test");
        await CreateGroupAsync(page, group);

        // Select the group → its (empty) Members section.
        await page.Locator(".wb-ug-rows").GetByText(group, new() { Exact = true }).ClickAsync();
        await Expect(page.GetByText("No members yet.")).ToBeVisibleAsync();

        // Add the user via the searchable picker.
        await page.Locator(".wb-ug-add-member input").First.FillAsync(user);
        await page.Locator(".mud-popover .mud-list-item").Filter(new() { HasText = user }).First.ClickAsync();

        var memberRow = page.Locator(".wb-ug-member").Filter(new() { HasText = user });
        await Expect(memberRow).ToBeVisibleAsync();

        // Remove it → the members list is empty again.
        await memberRow.GetByRole(AriaRole.Button).ClickAsync();
        await Expect(page.Locator(".wb-ug-member").Filter(new() { HasText = user })).Not.ToBeVisibleAsync();
    }

    private static async Task CreateUserAsync(IPage page, string displayName, string email)
    {
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).First.ClickAsync(); // New menu
        await page.GetByText("New user").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").Nth(0).FillAsync(email);
        await dialog.Locator("input").Nth(1).FillAsync(displayName);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await Expect(page.GetByText("User created.")).ToBeVisibleAsync();
    }

    private static async Task CreateGroupAsync(IPage page, string name)
    {
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).First.ClickAsync(); // New menu
        await page.GetByText("New group").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").First.FillAsync(name);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await Expect(page.GetByText("Group created.")).ToBeVisibleAsync();
    }
}
