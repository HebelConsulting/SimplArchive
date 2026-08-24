using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A target picker offers the same roots the tree shows (ADR 0689).
//
// The reported symptom: "the Move … context menu shows only the repository but not the Personal folder". The
// tree builds its roots from two sources — the personal space, fetched from the `me` resource, and
// GET /repositories, which deliberately EXCLUDES it — while every picker built its roots from the second
// alone. So the one place a person is most likely to be filing into was the one place they could not choose,
// while the tree beside the dialog showed it the whole time.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebFilingRootsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebFilingRootsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_move_picker_offers_the_personal_space_and_a_folder_inside_it()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var tree = page.Locator("[data-pane='tree']");
        var item = "movable-" + Guid.NewGuid().ToString("N")[..8];

        // The personal space's own name, read from the TREE — it is named after its owner (ADR 0671), so a
        // literal here would pin the wrong thing and pass for the wrong reason.
        var personalName = (await tree.Locator(".mud-treeview-item-content").First.InnerTextAsync()).Trim();

        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(item); };
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
        await Expect(list.GetByText(item)).ToBeVisibleAsync();

        await list.Locator(".wb-list-row").Filter(new() { HasText = item }).Locator("button").Last.ClickAsync();
        await page.GetByText("Move to").First.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        var personalRow = dialog.GetByText(personalName, new() { Exact = true }).First;
        await Expect(personalRow).ToBeVisibleAsync();

        // Selecting the personal ROOT commits nothing: its first level is provisioned, not user-filled (#634),
        // so the server would refuse — the button says so by staying unavailable rather than by failing after
        // the click.
        await personalRow.ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" })).ToBeDisabledAsync();

        // A folder INSIDE it is a real target, which is the whole point of showing the root. Expanded through
        // the row's own expander, so this exercises the personal space's advertised `children` address.
        //
        // "My Documents" by name, not whichever child comes first: the other provisioned folders are TYPED —
        // a calendar takes appointments, an addressbook contacts — so a plain folder moved into one of those is
        // refused by containment, and the test would fail for a reason that has nothing to do with the roots.
        await dialog.Locator(".mud-treeview-item-arrow button").First.ClickAsync();
        var inside = dialog.GetByText("My Documents", new() { Exact = true }).First;
        await Expect(inside).ToBeVisibleAsync();
        await inside.ClickAsync();

        var select = dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" });
        await Expect(select).ToBeEnabledAsync();
        await select.ClickAsync();

        // It left the repository — the move actually happened, rather than the dialog merely closing.
        await Expect(list.GetByText(item)).Not.ToBeVisibleAsync();
    }
}
