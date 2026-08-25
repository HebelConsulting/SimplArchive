using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
[Trait("Area", "ui-4")]
public class WebNotebookAffordanceTests
{
    private readonly SelfHostedAppFixture _app;

    public WebNotebookAffordanceTests(SelfHostedAppFixture app) => _app = app;

    // Expanding Personal reveals its typed folders; clicking the node would SELECT it, a different gesture.
    private static async Task<ILocator> ExpandPersonalAsync(IPage page)
    {
        var tree = page.Locator("[data-pane='tree']");
        var personal = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = SelfHostedAppFixture.AdminDisplayName }).First;
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

    // A notebook lives under the MAILBOX and nowhere else, and is not provisioned (#596, ADR 0634) — so these
    // tests make one over HTTP first. Two steps, both the real path: generating an IMAP credential materialises
    // the mailbox (the second of its two triggers, and the only one that does not require waiting for mail),
    // and the notebook is then asked for by mask.
    //
    // Deliberately NOT through the UI: neither client offers a "new notebook" affordance, because a notebook
    // without a notes client speaking IMAP is a folder whose purpose is unreachable.
    private async Task EnsureNotebookAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        (await http.PostAsJsonAsync("/api/me/imap-access", new { })).EnsureSuccessStatusCode();
        var personalId = (await (await http.PostAsJsonAsync("/api/me/personal-repository", new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var mailboxId = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox")
            .GetProperty("id").GetGuid();

        // Get-or-create: a mailbox holds at most ONE notebook, so the second test to run must find the first
        // test's rather than be refused. These tests share the demo user.
        var existing = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{mailboxId}/children"))
            .GetProperty("children").EnumerateArray()
            .Any(c => c.GetProperty("name").GetString() == "Notebook");

        if (!existing)
        {
            (await http.PostAsJsonAsync($"/api/documents/{mailboxId}/children",
                new { name = "Notebook", folderMask = "notes" })).EnsureSuccessStatusCode();
        }
    }

    // Personal → My Mailbox → Notebook. The notebook is a GRANDCHILD now, so the mailbox has to be expanded
    // too before its child is in the tree at all.
    private static async Task<ILocator> ExpandToNotebookAsync(IPage page, ILocator tree)
    {
        var mailbox = Node(tree, "My Mailbox");
        await Expect(mailbox).ToBeVisibleAsync();
        await mailbox.Locator(".mud-treeview-item-arrow").ClickAsync();

        var notebook = Node(tree, "Notebook");
        await Expect(notebook).ToBeVisibleAsync();
        return notebook;
    }

    [Fact]
    public async Task The_notebook_offers_both_creates_and_an_ordinary_folder_offers_neither()
    {
        await EnsureNotebookAsync();
        var page = await Ui.LoginAsync(_app);
        var tree = await ExpandPersonalAsync(page);

        var notebook = await ExpandToNotebookAsync(page, tree);
        await notebook.ClickAsync(new() { Button = MouseButton.Right });

        // Both creates live under "New" now (#673) and wear the MASK's name rather than a client string, so
        // what used to read "New section" reads "New ▸ Section" — and would read whatever this tenant renamed
        // the mask to.
        var menu = page.Locator(".mud-menu-item");
        var section = await Ui.OpenNewSubmenuAsync(page, "Section");
        await Expect(menu.Filter(new() { HasText = "Note" }).First).ToBeVisibleAsync();

        // The name prompt is the rename dialog reused, so its confirm button has to be RELABELLED — a create
        // whose button says "Rename" is a small lie the user reads before they read anything else.
        await section.ClickAsync();
        var prompt = page.Locator(".mud-dialog").First;
        await Expect(prompt.GetByRole(AriaRole.Button, new() { Name = "Create" })).ToBeVisibleAsync();
        await Expect(prompt.GetByRole(AriaRole.Button, new() { Name = "Rename" })).ToHaveCountAsync(0);

        // Dismiss with the dialog's OWN Cancel: MudBlazor does not close on Escape unless told to, and the
        // scrim keeps intercepting clicks meant for the tree behind it.
        await prompt.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(prompt).ToBeHiddenAsync();

        // My Documents is a plain folder: neither entry may appear. If this ever passes trivially because the
        // menu did not open, the "New ▸ Folder" assertion below catches it — a menu that is not there fails
        // that one too.
        var documents = Node(tree, "My Documents");
        await Expect(documents).ToBeVisibleAsync();
        await documents.ClickAsync(new() { Button = MouseButton.Right });

        // Its submenu holds the plain folder and nothing else — which is the containment rule reaching the
        // menu, not a client gate: a Notebook DECLARES it admits sections and notes, and an ordinary folder
        // declares nothing.
        await Ui.OpenNewSubmenuAsync(page, "Folder");
        await Expect(menu.Filter(new() { HasText = "Section" })).ToHaveCountAsync(0);
        await Expect(menu.Filter(new() { HasText = "Note" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task A_note_is_created_from_the_menu_with_a_title_and_a_body()
    {
        await EnsureNotebookAsync();
        var page = await Ui.LoginAsync(_app);
        var tree = await ExpandPersonalAsync(page);

        var notebook = await ExpandToNotebookAsync(page, tree);
        await notebook.ClickAsync(); // select it, so the contents list is showing when the note lands
        await notebook.ClickAsync(new() { Button = MouseButton.Right });
        await (await Ui.OpenNewSubmenuAsync(page, "Note")).ClickAsync();

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
