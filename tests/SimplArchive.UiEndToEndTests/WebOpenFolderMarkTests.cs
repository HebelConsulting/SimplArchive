using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The tree's ring marks the OPEN folder — where you are — not the row the list has selected (#686, revising
// what #696 shipped). The list already shows which of its own rows is selected, so a second marker for that was
// a state competing with a state (ADR 0581), and it changed meaning with the row type: a folder row moved the
// ring, a document row cleared it.
//
// With that settled, "nothing selected" needs a way back: Esc and a click on the list's empty area deselect,
// and the detail pane returns to describing the folder being stood in.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebOpenFolderMarkTests
{
    private readonly SelfHostedAppFixture _app;

    public WebOpenFolderMarkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Selecting_a_row_leaves_the_ring_on_the_folder_you_are_standing_in()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");
        var detail = page.Locator("[data-pane='index']");

        await tree.Locator(".mud-treeview-item-content")
            .Filter(new() { HasText = SelfHostedAppFixture.AdminDisplayName }).First.ClickAsync();

        // Where the ring is with the personal space open — captured rather than assumed, so the assertion below
        // is about it NOT MOVING rather than about any particular node.
        var ringed = tree.Locator(".wb-tree-current");
        await Expect(ringed).ToHaveCountAsync(1);
        var before = await ringed.TextContentAsync();

        // Select a row — any row. This used to move the ring onto it (a folder) or clear it (a document).
        var row = list.Locator(".wb-list-row").First;
        await Expect(row).ToBeVisibleAsync();
        var rowName = (await row.Locator(".wb-cname").First.TextContentAsync())!.Trim();
        await row.ClickAsync();

        // The detail pane follows the selection — waited for by CONTENT, not by the pane existing: it is
        // present throughout and shows nothing until the subject's fields arrive (ADR 0559), so "visible" is
        // true a beat before it means anything.
        await Expect(detail).ToContainTextAsync(rowName);

        // …and the tree does not. Still exactly one ring, still on the same node.
        await Expect(ringed).ToHaveCountAsync(1);
        Assert.Equal(before, await ringed.TextContentAsync());
    }

    [Theory]
    [InlineData(true)]   // Escape
    [InlineData(false)]  // a click on the list's empty area
    public async Task Deselecting_returns_the_detail_pane_to_the_open_folder(bool byEscape)
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");
        var detail = page.Locator("[data-pane='index']");

        await tree.Locator(".mud-treeview-item-content")
            .Filter(new() { HasText = SelfHostedAppFixture.AdminDisplayName }).First.ClickAsync();

        // With nothing selected the pane already describes the open folder — that is the subject we must come
        // back to. Asserted by NAME rather than by the pane's whole text: the pane also carries fields that
        // settle asynchronously, so comparing the entire string would be a test of load timing.
        await Expect(detail).ToBeVisibleAsync();
        var folderName = SelfHostedAppFixture.AdminDisplayName;
        await Expect(detail).ToContainTextAsync(folderName);

        var row = list.Locator(".wb-list-row").First;
        await Expect(row).ToBeVisibleAsync();
        var rowName = (await row.Locator(".wb-cname").First.TextContentAsync())!.Trim();
        await row.ClickAsync();
        await Expect(detail).ToContainTextAsync(rowName);

        if (byEscape)
        {
            await list.PressAsync("Escape");
        }
        else
        {
            // The pane's own background, deliberately below the rows: clicking a row would just select it.
            var box = await list.BoundingBoxAsync();
            Assert.NotNull(box);
            await page.Mouse.ClickAsync(box!.X + box.Width / 2, box.Y + box.Height - 12);
        }

        // Back to the folder. Before this there was no way to UNMAKE a selection: the pane could only move from
        // one subject to another, so a folder's own index data was unreachable without re-opening the folder.
        // Both halves asserted — the folder is described AND the row is not — because a pane that merely went
        // blank would satisfy the second on its own, and blank is the bug this replaced.
        await Expect(detail).ToContainTextAsync(folderName);
        await Expect(detail).Not.ToContainTextAsync(rowName);
    }
}
