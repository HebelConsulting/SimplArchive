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
    public async Task Personal_node_nests_intray_and_checkout_launchers_that_switch_tabs()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");

        // Expanding the Personal node reveals the Intray + Check-out launcher nodes (mirroring /webdav/Personal).
        var personal = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Personal" }).First;
        await Expect(personal).ToBeVisibleAsync();
        await personal.Locator(".mud-treeview-item-arrow").ClickAsync();

        var intray = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Intray" }).First;
        await Expect(intray).ToBeVisibleAsync();
        await Expect(tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Check-out" }).First).ToBeVisibleAsync();

        // Clicking the Intray launcher switches to the Intray bottom tab (its "Upload to intray" action appears).
        //
        // By aria-label, not by text: the toolbar shows icons only on a hover-capable device and carries the
        // label in `aria-label` + `title`, so the visible span is display:none here (ADR 0491 — touch, which
        // has no hover, still shows it). Asserting on the text asserted on the rendering rather than on the
        // control being there.
        //
        // And not by ROLE either, which finds nothing: this one is `HtmlTag="label"` (it opens the hidden file
        // input), so MudBlazor renders a <label>, and a label has no button role.
        await intray.ClickAsync();
        await Expect(page.Locator("[aria-label='Upload to intray']")).ToBeVisibleAsync();
    }
}
