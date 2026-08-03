using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0245/0250): the ⋮ menu Moves an item to another folder (it relocates) and Places a reference
// (a shortcut appears in another folder). Uses throwaway folders and always targets the repository root in the
// picker (no tree expansion needed), so it stays independent of the seeded content.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebMoveReferenceTests
{
    private readonly SelfHostedAppFixture _app;

    public WebMoveReferenceTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Move_relocates_an_item_and_place_reference_creates_a_shortcut()
    {
        var page = await Ui.LoginAsync(_app);
        var box = "box-" + Guid.NewGuid().ToString("N")[..8];
        var item = "item-" + Guid.NewGuid().ToString("N")[..8];
        var refItem = "ref-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        var nextFolderName = "";
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(nextFolderName); };

        async Task NewFolderAsync(string name)
        {
            nextFolderName = name;
            await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
            await Expect(list.GetByText(name)).ToBeVisibleAsync();
        }

        // A subfolder 'box' in the repo root, with 'item' inside it.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await NewFolderAsync(box);
        await list.GetByText(box).First.DblClickAsync();
        await NewFolderAsync(item);

        // MOVE 'item' out to the repository root.
        await OpenRowMenuAsync(list, item);
        await page.GetByText("Move to").First.ClickAsync();
        await PickRootAsync(page);
        await Expect(list.GetByText(item)).Not.ToBeVisibleAsync();     // gone from 'box'
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.GetByText(item)).ToBeVisibleAsync();          // now in the root

        // PLACE A REFERENCE: 'refItem' created in 'box', referenced into the root → a shortcut shows in the root.
        await list.GetByText(box).First.DblClickAsync();
        await NewFolderAsync(refItem);
        await OpenRowMenuAsync(list, refItem);
        await page.GetByText("Place reference in").First.ClickAsync();
        await PickRootAsync(page);
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.GetByText(refItem)).ToBeVisibleAsync();       // the shortcut appears in the root
    }

    private static async Task PickRootAsync(IPage page)
    {
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" }).ClickAsync();
    }

    private static async Task OpenRowMenuAsync(ILocator list, string name)
    {
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await row.Locator("button").Last.ClickAsync();
    }
}
