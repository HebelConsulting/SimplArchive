using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The per-user personal repository (ADR "Per-user personal repository") appears as a "Personal" node pinned in
// the workbench tree and is browsable/writable like any repository — selecting it and creating a folder into it
// round-trips.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebPersonalRepositoryTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPersonalRepositoryTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Personal_node_is_pinned_in_the_tree_and_is_browsable()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var folder = "e2e-personal-" + Guid.NewGuid().ToString("N")[..8];

        // The "Personal" node is present in the tree and selectable.
        var personal = tree.GetByText("Personal", new() { Exact = true }).First;
        await Expect(personal).ToBeVisibleAsync();
        await personal.ClickAsync();

        // It's a real writable repository — New folder files into it and the folder shows in the contents pane.
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(folder); };
        await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
        await Expect(page.Locator("[data-pane='list']").GetByText(folder)).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Personal_node_nests_inbox_and_checkout_launchers_that_switch_tabs()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");

        // Expanding the Personal node reveals the Inbox + Check-out launcher nodes (mirroring /webdav/Personal).
        var personal = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Personal" }).First;
        await Expect(personal).ToBeVisibleAsync();
        await personal.Locator(".mud-treeview-item-arrow").ClickAsync();

        var inbox = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Inbox" }).First;
        await Expect(inbox).ToBeVisibleAsync();
        await Expect(tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Check-out" }).First).ToBeVisibleAsync();

        // Clicking the Inbox launcher switches to the Inbox bottom tab (its "Upload to inbox" action appears).
        await inbox.ClickAsync();
        await Expect(page.GetByText("Upload to inbox")).ToBeVisibleAsync();
    }
}
