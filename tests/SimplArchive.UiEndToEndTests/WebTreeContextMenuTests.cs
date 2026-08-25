using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the tree-pane context menu (ADR "Tree-pane context menu with manage-access"): right-clicking a
// folder in the tree opens the same action set the desktop TreeView.ContextMenu offers — New subfolder / Rename /
// Delete / Manage access… / Follow folder / Refresh — acting on the node under the cursor. The interesting cases
// are (a) that the menu reaches Manage access at all, and (b) that a right-click on a NESTED folder targets that
// folder rather than its ancestor (a MudTreeViewItem renders its children inside itself, so the handler relies on
// stopPropagation). Target .mud-treeview-item-content so the filter hits the node's own label row, not an
// ancestor whose expanded subtree also contains the text.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebTreeContextMenuTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTreeContextMenuTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Right_clicking_a_repository_root_offers_the_desktop_parity_actions()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var root = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First;
        await Expect(root).ToBeVisibleAsync();

        await root.ClickAsync(new() { Button = MouseButton.Right });

        // The complete folder action set (the demo admin holds CanLegalHold + CanExport, so the gated entries
        // show too). "References…" is absent — nothing references this repository.
        var menu = page.Locator(".mud-menu-item");
        foreach (var label in new[]
                 {
                     "New", "Upload", "Rename", "Move to", "Contents sort order", "Delete",
                     "Manage access", "Place legal hold", "Place reference", "Follow", "Export", "Refresh",
                 })
        {
            await Expect(menu.Filter(new() { HasText = label }).First).ToBeVisibleAsync();
        }

        // "New" is a submenu now (#673), so its presence at the top level says nothing about whether it leads
        // anywhere — the creates the folder actually offers are inside it, labelled with the mask's own name.
        await Ui.OpenNewSubmenuAsync(page, "Folder");

        await Expect(menu.Filter(new() { HasText = "References" })).ToHaveCountAsync(0);

        await menu.Filter(new() { HasText = "Manage access" }).First.ClickAsync();
        await Expect(page.Locator(".mud-dialog").First).ToBeVisibleAsync();
    }

    // Touch fallback guard, mirroring WebTouchMoveReferenceTests: touch has no right-click, so the same menu must
    // be reachable by TAP from the row's ⋮ button. Driven under a HasTouch context by real taps.
    [Fact]
    public async Task The_row_menu_button_opens_the_same_menu_by_tap_on_touch()
    {
        var page = await Ui.LoginAsync(_app, configureContext: o => o.HasTouch = true);
        var tree = page.Locator("[data-pane='tree']");
        var root = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First;
        await Expect(root).ToBeVisibleAsync();

        // On a coarse pointer the button is visible without any hover — a hover-only affordance would be
        // unreachable on touch (ADR 0491).
        var menuButton = root.Locator(".wb-tree-menu-btn");
        await Expect(menuButton).ToBeVisibleAsync();

        await menuButton.TapAsync();

        var menu = page.Locator(".mud-menu-item");
        await Expect(menu.Filter(new() { HasText = "Manage access" }).First).ToBeVisibleAsync();
        await menu.Filter(new() { HasText = "Manage access" }).First.TapAsync();
        await Expect(page.Locator(".mud-dialog").First).ToBeVisibleAsync();
    }

    // The pseudo-nodes get no menu and therefore no button — Administration isn't a folder.
    [Fact]
    public async Task Pseudo_nodes_get_no_row_menu_button()
    {
        var page = await Ui.LoginAsync(_app, configureContext: o => o.HasTouch = true);
        var tree = page.Locator("[data-pane='tree']");
        var admin = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Administration" }).First;
        await Expect(admin).ToBeVisibleAsync();

        await Expect(admin.Locator(".wb-tree-menu-btn")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Right_clicking_a_subfolder_manages_that_subfolders_acl_not_its_parents()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var folderName = $"ctx-{suffix}";
        var granteeName = $"CtxGrantee {suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var folderId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = folderName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A grant that exists ONLY on the subfolder — if the dialog shows it, it loaded the subfolder's ACL and
        // not the repository root's.
        var granteeId = (await (await http.PostAsJsonAsync("/api/users", new { email = $"ctx-{suffix}@simplarchive.local", displayName = granteeName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await http.PutAsJsonAsync($"/api/documents/{folderId}/acl-entries/users/{granteeId}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var root = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First;
        await Expect(root).ToBeVisibleAsync();
        await root.Locator(".mud-treeview-item-arrow").ClickAsync();

        var subfolder = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = folderName }).First;
        await Expect(subfolder).ToBeVisibleAsync(new() { Timeout = 15000 });
        await subfolder.ClickAsync(new() { Button = MouseButton.Right });

        await page.Locator(".mud-menu-item").Filter(new() { HasText = "Manage access" }).First.ClickAsync();

        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByText(granteeName).First).ToBeVisibleAsync();
    }
}
