using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "Users & groups administration tab"): the Users & groups tab lists users (one-person icon)
// and groups (two-person icon) combined, with the assignable system-rights matrix on the right and
// New/Copy/Delete. The demo admin is a tenant admin with CanManageUsers, so the tab is visible and it can
// grant the rights it holds (Manage repositories / Manage masks).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebUsersGroupsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebUsersGroupsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Creates_a_user_and_a_group_and_persists_an_assigned_right()
    {
        var page = await Ui.LoginAsync(_app);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = "ug-user-" + suffix;
        var groupName = "ug-group-" + suffix;

        await OpenTabAsync(page);

        // New user → appears in the combined list.
        await CreateUserAsync(page, userName, $"{userName}@example.test");
        var rows = page.Locator(".wb-ug-rows");
        await Expect(rows.GetByText(userName)).ToBeVisibleAsync();

        // New group → appears too.
        await CreateGroupAsync(page, groupName);
        await Expect(rows.GetByText(groupName)).ToBeVisibleAsync();

        // Select the user: the rights matrix is read-only until Edit. Click Edit, grant "Manage repositories"
        // (a right the admin holds), then Save.
        await rows.GetByText(userName).ClickAsync();
        await Expect(page.Locator(".wb-ug-right").Filter(new() { HasText = "Manage repositories" }).Locator("input")).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await page.Locator(".wb-ug-right").Filter(new() { HasText = "Manage repositories" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Rights saved.")).ToBeVisibleAsync();

        // Re-enter the tab (forces a reload) and confirm the right persisted.
        await page.Locator(".wb-tab").Filter(new() { HasText = "Repositories" }).First.ClickAsync();
        await OpenTabAsync(page);
        await page.Locator(".wb-ug-rows").GetByText(userName).ClickAsync();
        await Expect(page.Locator(".wb-ug-right").Filter(new() { HasText = "Manage repositories" }).Locator("input")).ToBeCheckedAsync();
    }

    [Fact]
    public async Task Copy_group_duplicates_its_rights_and_delete_removes_it()
    {
        var page = await Ui.LoginAsync(_app);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = "ug-src-" + suffix;
        var copy = "ug-copy-" + suffix;
        var rows = page.Locator(".wb-ug-rows");

        await OpenTabAsync(page);

        // A source group with "Manage masks" granted (Edit → toggle → Save).
        await CreateGroupAsync(page, source);
        await rows.GetByText(source).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await page.Locator(".wb-ug-right").Filter(new() { HasText = "Manage masks" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Rights saved.")).ToBeVisibleAsync();

        // Copy → the dialog is pre-filled from the source; rename it and create.
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).Nth(1).ClickAsync(); // Copy button
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").First.FillAsync(copy);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();

        // The copy carries the source's "Manage masks" right.
        await rows.GetByText(copy).ClickAsync();
        await Expect(page.Locator(".wb-ug-right").Filter(new() { HasText = "Manage masks" }).Locator("input")).ToBeCheckedAsync();

        // Delete the copy → it leaves the list.
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).Nth(2).ClickAsync(); // Delete button
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(rows.GetByText(copy)).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task Rights_matrix_is_readonly_until_edit_and_cancel_reverts()
    {
        var page = await Ui.LoginAsync(_app);
        await OpenTabAsync(page);

        // Select the first principal — the matrix is read-only (disabled).
        await page.Locator(".wb-ug-rows .wb-list-row").First.ClickAsync();
        var right = page.Locator(".wb-ug-right").Filter(new() { HasText = "Manage classification" });
        await Expect(right.Locator("input")).ToBeDisabledAsync();

        // Edit → the matrix becomes editable; Cancel → read-only again (no save).
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await Expect(right.Locator("input")).ToBeEnabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(right.Locator("input")).ToBeDisabledAsync();
    }

    // The dedicated Export/Import rights (ADR "Dedicated CanExport/CanImport rights") appear in the rights
    // matrix and a granted "Export" persists across a reload — the demo admin holds Export/Import (granted at
    // provisioning), so it can grant them.
    [Fact]
    public async Task Grants_the_export_right_and_it_persists()
    {
        var page = await Ui.LoginAsync(_app);
        var userName = "eir-user-" + Guid.NewGuid().ToString("N")[..8];

        await OpenTabAsync(page);
        await CreateUserAsync(page, userName, $"{userName}@example.test");
        var rows = page.Locator(".wb-ug-rows");
        await Expect(rows.GetByText(userName)).ToBeVisibleAsync();

        await rows.GetByText(userName).ClickAsync();
        // Both new matrix rows render.
        await Expect(page.Locator(".wb-ug-right").Filter(new() { HasText = "Export" }).First).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-ug-right").Filter(new() { HasText = "Import" }).First).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await page.Locator(".wb-ug-right").Filter(new() { HasText = "Export" }).First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Rights saved.")).ToBeVisibleAsync();

        await page.Locator(".wb-tab").Filter(new() { HasText = "Repositories" }).First.ClickAsync();
        await OpenTabAsync(page);
        await page.Locator(".wb-ug-rows").GetByText(userName).ClickAsync();
        await Expect(page.Locator(".wb-ug-right").Filter(new() { HasText = "Export" }).First.Locator("input")).ToBeCheckedAsync();
    }

    private static async Task OpenTabAsync(IPage page)
    {
        await page.Locator(".wb-tab").Filter(new() { HasText = "Users & groups" }).First.ClickAsync();
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
    }

    private static async Task CreateGroupAsync(IPage page, string name)
    {
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).First.ClickAsync(); // New menu
        await page.GetByText("New group").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").First.FillAsync(name);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
    }
}
