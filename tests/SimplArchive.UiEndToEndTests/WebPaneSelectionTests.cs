using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The three panes describe ONE subject (#686): selecting a folder in the contents list reveals it in the tree
// and describes it in the detail pane, without opening it.
//
// The distinction under test is the one the fix turns on — SELECTED and OPEN are two facts. A folder can be
// marked in the tree while the list keeps showing its parent's contents, which is what lets a user read a
// folder's metadata without losing the listing they are standing in. Asserting only the highlight would pass
// just as well if selecting had NAVIGATED, which is the outcome these have to rule out.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebPaneSelectionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPaneSelectionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Selecting_a_folder_reveals_it_in_the_tree_without_opening_it()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.Locator(".wb-list-row").First).ToBeVisibleAsync();

        // The repository is what is marked to begin with — clicking it in the tree both opened and selected it.
        // Establishing that first is what makes the assertion below about the mark MOVING rather than merely
        // existing, which a tree that marked everything would also satisfy.
        await Expect(tree.Locator(".wb-tree-current")).ToContainTextAsync("Demo Repository");

        await list.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" }).First.ClickAsync();

        // Revealed: the branch above it opened, and the node itself is marked as current. Visibility is asserted
        // separately from the class, because the first attempt at this set the class on a node inside a branch
        // that stayed shut — present in the DOM, and invisible.
        var marked = tree.Locator(".wb-tree-current");
        await Expect(marked).ToHaveCountAsync(1);
        await Expect(marked).ToContainTextAsync("Contracts");
        // Exactly one, and it moved OFF the repository — two marked nodes would be two claims about which
        // subject the panes describe.
        await Expect(marked).Not.ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^Demo Repository"));
        await Expect(tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Contracts" }).First).ToBeVisibleAsync();

        // NOT opened: the list still shows the folder the user is standing in. This is the half that would be
        // lost by making selection navigate, and it is why reveal and open are separate acts.
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = "Departments" })).ToHaveCountAsync(1);

        // ...and the detail pane describes the SELECTED folder, not the open one.
        await Expect(page.Locator(".wb-index")).ToContainTextAsync("Contracts");
    }

    // Double-click is the act that opens, and it must still do both — select AND navigate — or the separation
    // above would have cost the user the only way in.
    [Fact]
    public async Task Double_clicking_a_folder_still_opens_it()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.Locator(".wb-list-row").First).ToBeVisibleAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" }).First.DblClickAsync();

        // The listing changed to the folder's own contents — "Departments" was a sibling and is gone.
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = "Acme Corp" }).First).ToBeVisibleAsync();
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = "Departments" })).ToHaveCountAsync(0);
    }
}
