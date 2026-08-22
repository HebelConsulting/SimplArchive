using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The tenant-admin Administration → Users view (ADR "Tenant-admin Administration → Users view"): a synthetic
// "Administration → Users" branch is pinned in the demo admin's repository tree, and expanding it reveals the
// users' personal repositories (the admin's own "Demo Admin" personal space at least).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebAdminUsersTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAdminUsersTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Administration_users_branch_lists_personal_repositories()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");

        // The admin-only Administration node is pinned in the tree; expand it → Users → the users' personal repos.
        // Target the content row (.mud-treeview-item-content) so filtering by text hits the node's own label, not
        // an ancestor whose expanded subtree also contains the text.
        var admin = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Administration" }).First;
        await Expect(admin).ToBeVisibleAsync();
        await admin.Locator(".mud-treeview-item-arrow").ClickAsync();

        var users = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Users" }).First;
        await Expect(users).ToBeVisibleAsync();
        await users.Locator(".mud-treeview-item-arrow").ClickAsync();

        // The demo admin's own personal space appears under Users (loaded from the admin endpoint).
        //
        // Scoped to the Users SUBTREE, and that is the whole assertion. Unscoped, this matched two nodes once
        // personal spaces began carrying their owner's name (ADR 0671): the admin's own space at the tree's top
        // level, and its entry here. Playwright then raised a strict-mode violation — but only once the branch
        // had rendered, so the test PASSED whenever the assertion ran early enough to match the top-level node
        // alone. It was green by matching the wrong element, which is worse than red.
        var usersSubtree = users.Locator("xpath=ancestor::li[contains(@class,'mud-treeview-item')][1]");
        await Expect(usersSubtree.GetByText("Demo Admin")).ToBeVisibleAsync(new() { Timeout = 15000 });
    }
}
