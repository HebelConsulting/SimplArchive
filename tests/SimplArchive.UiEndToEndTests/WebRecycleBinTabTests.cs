using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "Recycle bin tab"): the dedicated Recycle bin tab lists deleted items tenant-wide; the demo
// admin selects one (detail pane loads), hard-deletes it from its row (no extra confirm), and the "Hard-delete
// all" dialog gates OK behind typing "I AGREE" (cancelled here so the shared bin isn't emptied).
//
// Uses a throwaway FOLDER (no blob), not an uploaded document: with per-tenant object-lock buckets (ADR
// "Per-tenant object-storage bucket") the demo tenant's Basic-Entry 7-year retention now genuinely WORM-locks an
// uploaded document's blob, so hard-deleting it is correctly refused — that path is covered by the API-level
// WORM/purge E2E tests. A folder has no blob, so it purges cleanly, exercising the recycle-bin UI flow.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebRecycleBinTabTests
{
    private readonly SelfHostedAppFixture _app;

    public WebRecycleBinTabTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Recycle_bin_tab_lists_selects_and_hard_deletes()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "rbtab-" + Guid.NewGuid().ToString("N")[..8];

        // Create then delete a throwaway folder (no blob — see the class note).
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(name); };
        await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
        await Expect(list.GetByText(name)).ToBeVisibleAsync();
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("Delete").First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(list.GetByText(name)).Not.ToBeVisibleAsync(); // the soft-delete has committed

        // Recycle bin tab → the item is listed; select it (detail pane loads).
        await page.Locator(".wb-tab[aria-label=\"Recycle bin\"]").First.ClickAsync();
        var bin = page.Locator(".wb-recyclebin");
        await Expect(bin).ToBeVisibleAsync();
        var binRow = bin.Locator("tr").Filter(new() { HasText = name });
        await Expect(binRow).ToBeVisibleAsync();
        await binRow.ClickAsync();
        await Expect(bin.GetByText(name).Last).ToBeVisibleAsync();

        // The "Hard-delete all" dialog gates OK behind "I AGREE".
        await bin.GetByRole(AriaRole.Button, new() { Name = "Hard-delete all" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.GetByText("All listed documents will be unrecoverably deleted.")).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "OK" })).ToBeDisabledAsync();
        await dialog.Locator("input").First.FillAsync("I AGREE");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "OK" })).ToBeEnabledAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        // Hard-delete the specific item from its row's ⋮ menu (#530: per-row buttons became the menu) →
        // it's gone from the bin.
        await binRow.Locator("button.mud-icon-button").Last.ClickAsync();
        await page.Locator(".mud-menu-item").Filter(new() { HasText = "Hard-delete" }).First.ClickAsync();
        await Expect(bin.Locator("tr").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task Bulk_restore_checks_rows_and_restores_them()
    {
        var page = await Ui.LoginAsync(_app);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var names = new[] { $"rbbulk-a-{tag}", $"rbbulk-b-{tag}" };

        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        // A single dialog handler reading a mutable name — registering one per loop iteration would accumulate,
        // and the first (stale) handler would accept the next prompt with the wrong name.
        var currentFolderName = "";
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(currentFolderName); };
        foreach (var name in names)
        {
            currentFolderName = name;
            await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
            await Expect(list.GetByText(name)).ToBeVisibleAsync();
        }

        // Delete both folders.
        foreach (var name in names)
        {
            var r = list.Locator(".wb-list-row").Filter(new() { HasText = name });
            await r.Locator("button").Last.ClickAsync();
            await page.GetByText("Delete").First.ClickAsync();
            await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await Expect(list.GetByText(name)).Not.ToBeVisibleAsync();
        }

        // Recycle bin tab → Ctrl-click both rows into the native multi-selection (#530: the checkbox column
        // is the TOUCH affordance and is hidden on a hover-capable pointer device, which Playwright's Chrome
        // is). A synthetic click carrying ctrlKey — real-input modifier clicks do not reach Blazor.
        await page.Locator(".wb-tab[aria-label=\"Recycle bin\"]").First.ClickAsync();
        var bin = page.Locator(".wb-recyclebin");
        await Expect(bin).ToBeVisibleAsync();
        var ctrl = new Dictionary<string, object> { ["ctrlKey"] = true, ["bubbles"] = true };
        foreach (var name in names)
        {
            var binRow = bin.Locator("tr").Filter(new() { HasText = name });
            await Expect(binRow).ToBeVisibleAsync();
            await binRow.DispatchEventAsync("click", ctrl);
        }

        // The toolbar's restore is icon-only; its tooltip carries the counted label.
        await bin.Locator("[aria-label^='Restore selected']").First.ClickAsync();

        // Both leave the recycle bin and are back in the repository.
        foreach (var name in names)
        {
            await Expect(bin.Locator("tr").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task Bulk_purge_checks_rows_gated_by_I_AGREE_and_removes_them()
    {
        var page = await Ui.LoginAsync(_app);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var names = new[] { $"rbpurge-a-{tag}", $"rbpurge-b-{tag}" };

        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        // Single dialog handler reading a mutable name (registering one per iteration would accumulate).
        var currentFolderName = "";
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(currentFolderName); };
        foreach (var name in names)
        {
            currentFolderName = name;
            await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
            await Expect(list.GetByText(name)).ToBeVisibleAsync();
        }

        foreach (var name in names)
        {
            var r = list.Locator(".wb-list-row").Filter(new() { HasText = name });
            await r.Locator("button").Last.ClickAsync();
            await page.GetByText("Delete").First.ClickAsync();
            await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await Expect(list.GetByText(name)).Not.ToBeVisibleAsync();
        }

        // Recycle bin tab → check both rows, then "Purge selected" → the "I AGREE" gate.
        await page.Locator(".wb-tab[aria-label=\"Recycle bin\"]").First.ClickAsync();
        var bin = page.Locator(".wb-recyclebin");
        await Expect(bin).ToBeVisibleAsync();
        var ctrl = new Dictionary<string, object> { ["ctrlKey"] = true, ["bubbles"] = true };
        foreach (var name in names)
        {
            var binRow = bin.Locator("tr").Filter(new() { HasText = name });
            await Expect(binRow).ToBeVisibleAsync();
            await binRow.DispatchEventAsync("click", ctrl); // #530: native multi-select; checkboxes are touch-only
        }

        await bin.Locator("[aria-label^='Purge selected']").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").First.FillAsync("I AGREE");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "OK" }).ClickAsync();

        // Both are permanently gone from the bin.
        foreach (var name in names)
        {
            await Expect(bin.Locator("tr").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();
        }
    }
}
