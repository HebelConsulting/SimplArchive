using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow: a folder round-trips through the workbench — New folder (window.prompt) → Rename (dialog) →
// Delete (confirm → recycle bin) → Restore (recycle-bin dialog). Uses its own throwaway folder so it doesn't
// disturb the seeded content other tests rely on.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebLifecycleTests
{
    private readonly SelfHostedAppFixture _app;

    public WebLifecycleTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Folder_can_be_created_renamed_deleted_and_restored()
    {
        var page = await Ui.LoginAsync(_app);
        var folder = "e2e-crud-" + Guid.NewGuid().ToString("N")[..8];
        var renamed = "e2e-renamed-" + Guid.NewGuid().ToString("N")[..8];

        // Select the repository root so New folder / Recycle bin are enabled.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");

        // New folder — the name comes from a window.prompt.
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(folder); };
        await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
        await Expect(list.GetByText(folder)).ToBeVisibleAsync();

        // Rename via the row's ⋮ menu → dialog.
        await OpenRowMenuAsync(list, folder);
        await page.GetByText("Rename").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").First.FillAsync(renamed);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Rename" }).ClickAsync();
        await Expect(list.GetByText(renamed)).ToBeVisibleAsync();

        // Delete via the ⋮ menu → confirm message box.
        await OpenRowMenuAsync(list, renamed);
        await page.GetByText("Delete").First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(list.GetByText(renamed)).Not.ToBeVisibleAsync();

        // Recycle bin tab → the deleted folder is listed → Restore removes it from the bin. Scope to this
        // folder's own row — the tenant-wide bin is shared across the collection.
        await page.Locator(".wb-tab[aria-label=\"Recycle bin\"]").First.ClickAsync();
        var bin = page.Locator(".wb-recyclebin");
        var renamedRow = bin.Locator("tr").Filter(new() { HasText = renamed });
        await Expect(renamedRow).ToBeVisibleAsync();
        // #530: the per-row Restore button became the row's ⋮ menu entry.
        await renamedRow.Locator("button.mud-icon-button").Last.ClickAsync();
        await page.Locator(".mud-menu-item").Filter(new() { HasText = "Restore" }).First.ClickAsync();
        await Expect(bin.Locator("tr").Filter(new() { HasText = renamed })).Not.ToBeVisibleAsync();
    }

    private static async Task OpenRowMenuAsync(ILocator list, string name)
    {
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await row.Locator("button").Last.ClickAsync();
    }
}
