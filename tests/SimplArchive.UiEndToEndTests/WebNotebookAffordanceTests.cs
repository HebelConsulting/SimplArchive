using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// "New section" / "New note" on the web tree context menu (#564, ADR 0625) — the twin of the desktop entries,
// gated the same way and by the same thing: the rels the ROW advertised.
//
// The absence case is the one that earns its keep. Both entries are trivially "present" if the client renders
// them unconditionally, and the feature would still look right on the folder it was built for — so the test
// that matters is the one asserting they are NOT on an ordinary folder.
[Collection(UiCollection.Name)]
public class WebNotebookAffordanceTests
{
    private readonly SelfHostedAppFixture _app;

    public WebNotebookAffordanceTests(SelfHostedAppFixture app) => _app = app;

    // Expanding Personal reveals its typed folders; clicking the node would SELECT it, a different gesture.
    private static async Task<ILocator> ExpandPersonalAsync(IPage page)
    {
        var tree = page.Locator("[data-pane='tree']");
        var personal = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Personal" }).First;
        await Expect(personal).ToBeVisibleAsync();
        await personal.Locator(".mud-treeview-item-arrow").ClickAsync();
        return tree;
    }

    // MudTextField (no Immediate) commits on BLUR, so a fill that never blurs leaves the model empty.
    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.EvaluateAsync("el => el.blur()");
    }

    private static ILocator Node(ILocator tree, string name) =>
        tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = name }).First;

    [Fact]
    public async Task The_notebook_offers_both_creates_and_an_ordinary_folder_offers_neither()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = await ExpandPersonalAsync(page);

        var notebook = Node(tree, "Notebook");
        await Expect(notebook).ToBeVisibleAsync();
        await notebook.ClickAsync(new() { Button = MouseButton.Right });

        var menu = page.Locator(".mud-menu-item");
        await Expect(menu.Filter(new() { HasText = "New section" }).First).ToBeVisibleAsync();
        await Expect(menu.Filter(new() { HasText = "New note" }).First).ToBeVisibleAsync();

        // The name prompt is the rename dialog reused, so its confirm button has to be RELABELLED — a create
        // whose button says "Rename" is a small lie the user reads before they read anything else.
        await menu.Filter(new() { HasText = "New section" }).First.ClickAsync();
        var prompt = page.Locator(".mud-dialog").First;
        await Expect(prompt.GetByRole(AriaRole.Button, new() { Name = "Create" })).ToBeVisibleAsync();
        await Expect(prompt.GetByRole(AriaRole.Button, new() { Name = "Rename" })).ToHaveCountAsync(0);

        // Dismiss with the dialog's OWN Cancel: MudBlazor does not close on Escape unless told to, and the
        // scrim keeps intercepting clicks meant for the tree behind it.
        await prompt.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(prompt).ToBeHiddenAsync();

        // My Documents is a plain folder: neither entry may appear. If this ever passes trivially because the
        // menu did not open, the "New subfolder" assertion below catches it — a menu that is not there fails
        // that one too.
        var documents = Node(tree, "My Documents");
        await Expect(documents).ToBeVisibleAsync();
        await documents.ClickAsync(new() { Button = MouseButton.Right });

        await Expect(menu.Filter(new() { HasText = "New subfolder" }).First).ToBeVisibleAsync();
        await Expect(menu.Filter(new() { HasText = "New section" })).ToHaveCountAsync(0);
        await Expect(menu.Filter(new() { HasText = "New note" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task A_note_is_created_from_the_menu_with_a_title_and_a_body()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = await ExpandPersonalAsync(page);

        var notebook = Node(tree, "Notebook");
        await Expect(notebook).ToBeVisibleAsync();
        await notebook.ClickAsync(); // select it, so the contents list is showing when the note lands
        await notebook.ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator(".mud-menu-item").Filter(new() { HasText = "New note" }).First.ClickAsync();

        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();

        // Title and body in ONE step — a note filed with an empty body would be indistinguishable from a
        // mistake in both the tree and a notes client.
        var title = "web-note-" + Guid.NewGuid().ToString("N")[..8];
        await FillMudAsync(dialog.Locator("input").First, title);
        await FillMudAsync(dialog.Locator("textarea").First, "Milk\nBread");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();

        // It appears in the folder it was filed into, under its title.
        await Expect(page.Locator("[data-pane='list']").GetByText(title).First).ToBeVisibleAsync();
    }
}
