using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Touch fallback guard (ADR 0531 touch note): the internal move/reference DRAG gesture is native HTML5 DnD, which
// doesn't fire on touch — so on touch the equivalent is the ⋮ row menu's "Move to…" / "Place reference in…" (and
// the bulk-move bar). This test drives that path by real TAP under a HasTouch context, proving move + reference
// are fully usable on touch without the drag gesture. (Layout responsiveness is guarded separately by
// WebTouchChromeTests; this guards touch INPUT reaching the move/reference actions.)
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebTouchMoveReferenceTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTouchMoveReferenceTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Move_and_place_reference_work_by_tap_on_touch()
    {
        var page = await Ui.LoginAsync(_app, configureContext: o => o.HasTouch = true);
        var box = "tbox-" + Guid.NewGuid().ToString("N")[..8];
        var item = "titem-" + Guid.NewGuid().ToString("N")[..8];
        var refItem = "tref-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        var nextFolderName = "";
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(nextFolderName); };

        async Task NewFolderAsync(string name)
        {
            nextFolderName = name;
            await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.TapAsync();
            await Expect(list.GetByText(name)).ToBeVisibleAsync();
        }

        // A subfolder 'box' in the repo root, with 'item' inside it.
        await page.GetByText("Demo Repository").First.TapAsync();
        await NewFolderAsync(box);
        await list.GetByText(box).First.DblClickAsync(); // double-tap to open the folder
        await NewFolderAsync(item);

        // MOVE 'item' out to the repository root — by tap, via the ⋮ menu (the touch equivalent of drag).
        await OpenRowMenuAsync(list, item);
        await page.GetByText("Move to").First.TapAsync();
        await PickRootAsync(page);
        await Expect(list.GetByText(item)).Not.ToBeVisibleAsync();     // gone from 'box'
        await page.GetByText("Demo Repository").First.TapAsync();
        await Expect(list.GetByText(item)).ToBeVisibleAsync();          // now in the root

        // PLACE A REFERENCE by tap: 'refItem' in 'box', referenced into the root → a shortcut shows in the root.
        await list.GetByText(box).First.DblClickAsync();
        await NewFolderAsync(refItem);
        await OpenRowMenuAsync(list, refItem);
        await page.GetByText("Place reference in").First.TapAsync();
        await PickRootAsync(page);
        await page.GetByText("Demo Repository").First.TapAsync();
        await Expect(list.GetByText(refItem)).ToBeVisibleAsync();       // the shortcut appears in the root
    }

    private static async Task PickRootAsync(IPage page)
    {
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.TapAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" }).TapAsync();
    }

    private static async Task OpenRowMenuAsync(ILocator list, string name)
    {
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await row.Locator("button").Last.TapAsync();
    }
}
