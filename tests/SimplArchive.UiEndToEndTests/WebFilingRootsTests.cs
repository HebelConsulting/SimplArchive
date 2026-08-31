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

    [Fact]
    public async Task A_typed_collection_is_shown_but_cannot_be_picked_as_a_target()
    {
        // Reported against the Intray's file dialog: it offered "My Addressbook" and "My Calendar" as places to
        // file a PDF. They hold contacts and appointments, so the server refuses — the picker had every node's
        // `create-child` rel in hand and never read it, which is the exact promise ADR 0543 exists to keep.
        //
        // The knowledge was already here: the test above names "My Documents" explicitly, with a comment saying
        // the typed folders would be "refused by containment". The workaround was written and the defect it was
        // working around was not seen.
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var item = "typed-" + Guid.NewGuid().ToString("N")[..8];

        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(item); };
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
        await Expect(list.GetByText(item)).ToBeVisibleAsync();

        await list.Locator(".wb-list-row").Filter(new() { HasText = item }).Locator("button").Last.ClickAsync();
        await page.GetByText("Move to").First.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        var select = dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" });
        await dialog.Locator(".mud-treeview-item-arrow button").First.ClickAsync();

        // A typed collection is still SHOWN — a user browsing to something inside it must be able to get there
        // (ADR 0689) — but it cannot be the target.
        var calendar = dialog.GetByText("My Calendar", new() { Exact = true }).First;
        await Expect(calendar).ToBeVisibleAsync();
        await calendar.ClickAsync();
        await Expect(select).ToBeDisabledAsync();

        // The control, without which this test would pass on a picker that disabled EVERYTHING: a plain folder
        // in the same list, reached the same way, stays a real target.
        var documents = dialog.GetByText("My Documents", new() { Exact = true }).First;
        await documents.ClickAsync();
        await Expect(select).ToBeEnabledAsync();
    }
}
