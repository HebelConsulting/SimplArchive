using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Returning to the Repositories tab with an Administration node selected keeps listing it — it does not claim
// the user cannot see it.
//
// WHAT WAS WRONG. A synthetic Administration node has contents but no `children` rel, so the refresh path
// asked the API for children it does not have, failed, and showed **"You do not have access to this folder."**
// — a refusal the server never made, about a node the user was looking at and plainly could see.
//
// WHY #1160'S OWN TESTS MISSED IT. `SelectFolderAsync` already had the synthetic-node branch, so SELECTING an
// admin node worked; only REFRESHING one did not, and the refresh runs on returning to the tab, on filing a
// version, and on saving index data. Every test drove selection. The gap was a whole second entry point that
// nothing exercised — which is why this test leaves the tab and comes back rather than asserting on the
// selection alone.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebAdminTabReturnTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAdminTabReturnTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Returning_to_Repositories_with_an_admin_node_selected_still_lists_it()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        // Target the content row, not the item: filtering by text on the item also matches an ancestor whose
        // expanded subtree contains the word (the lesson WebAdminUsersTests records).
        var administration = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Administration" }).First;
        await Expect(administration).ToBeVisibleAsync();
        await administration.ClickAsync();
        await Expect(list.GetByText("Users", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 15000 });

        // Leave, then come back by clicking the Repositories tab icon — the reported reproduction exactly.
        // Tabs are icon-only on a hover-capable device (#298), so the label lives in `aria-label` and not in
        // visible text: address them the way WebTabBarTests does rather than by role+name.
        await page.Locator(".wb-tab[aria-label='Tasks']").First.ClickAsync();
        await Expect(list.GetByText("Users", new() { Exact = true })).ToHaveCountAsync(0);
        await page.Locator(".wb-tab[aria-label='Repositories']").First.ClickAsync();

        // BOTH halves, and the second is the one that regresses quietly. The contents must come back...
        await Expect(list.GetByText("Users", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 15000 });

        // ...AND no refusal may be shown. Asserting only on the rows would pass against a build that lists the
        // node and shows the snackbar anyway — which is a worse experience than either, because the user is
        // told they cannot see something that is on screen in front of them.
        await Expect(page.Locator(".mud-snackbar").Filter(new() { HasText = "access" })).ToHaveCountAsync(0);
    }
}
