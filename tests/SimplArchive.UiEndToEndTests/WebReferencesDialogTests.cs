using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0246): after a reference to an item exists, the item's ⋮ "References…" opens a dialog listing
// the folders that reference it. Self-contained folders; references into the repo root (no picker expansion).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebReferencesDialogTests
{
    private readonly SelfHostedAppFixture _app;

    public WebReferencesDialogTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task References_dialog_lists_the_referencing_folder()
    {
        var page = await Ui.LoginAsync(_app);
        var box = "rbox-" + Guid.NewGuid().ToString("N")[..8];
        var item = "rme-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        var nextFolderName = "";
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(nextFolderName); };

        async Task NewFolderAsync(string name)
        {
            nextFolderName = name;
            await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
            await Expect(list.GetByText(name)).ToBeVisibleAsync();
        }

        // 'item' inside 'box'; reference it into the repo root.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await NewFolderAsync(box);
        await list.GetByText(box).First.DblClickAsync();
        await NewFolderAsync(item);

        await OpenRowMenuAsync(page, list, item);
        await page.GetByText("Place reference in").First.ClickAsync();
        await PickRootAsync(page);

        // Re-open 'box' so the item's HasReferences (recomputed server-side) is reflected.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await list.GetByText(box).First.DblClickAsync();
        await Expect(list.GetByText(item)).ToBeVisibleAsync(); // the box's contents must finish loading first

        // The item now has a reference → its ⋮ "References…" lists the referencing folder (the root).
        await OpenRowMenuAsync(page, list, item);
        await page.GetByText("References").First.ClickAsync();
        await Expect(page.Locator(".mud-dialog").GetByText("Demo Repository").First).ToBeVisibleAsync();
    }

    private static async Task PickRootAsync(IPage page)
    {
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" }).ClickAsync();
    }

    // Opens a contents-row's ⋮ menu. On a slow/loaded runner the row can re-render right as the ⋮ is clicked, so
    // the click is swallowed and no menu opens — retry until a menu item ("Delete", present on every row) shows.
    private static async Task OpenRowMenuAsync(IPage page, ILocator list, string name)
    {
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        var menuButton = row.Locator("button").Last;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await menuButton.ClickAsync();
            try
            {
                await Expect(page.GetByText("Delete").First).ToBeVisibleAsync(new() { Timeout = 1500 });
                return;
            }
            catch (PlaywrightException)
            {
                // Menu didn't open (click swallowed by a re-render) — retry.
            }
        }
    }
}
