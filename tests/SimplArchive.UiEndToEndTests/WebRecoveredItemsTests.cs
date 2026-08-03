using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0196): restoring a child whose parent is still deleted reparents it into an auto-created
// "Recovered Items" folder under the repository root, rather than back into the (still-deleted) parent.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebRecoveredItemsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebRecoveredItemsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Restoring_a_child_whose_parent_is_gone_recovers_it()
    {
        var page = await Ui.LoginAsync(_app);
        var parent = "recov-parent-" + Guid.NewGuid().ToString("N")[..8];
        var child = "recov-child-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        // New folder names come from a window.prompt — a mutable capture supplies each in turn.
        var nextFolder = parent;
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(nextFolder); };

        // Create the parent folder at the repository root.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator(".wb-ribbon").GetByText("New folder").First.ClickAsync();
        await Expect(list.GetByText(parent)).ToBeVisibleAsync();

        // Drill into the parent (double-click) and create the child folder inside it.
        nextFolder = child;
        await list.GetByText(parent).First.DblClickAsync();
        await page.Locator(".wb-ribbon").GetByText("New folder").First.ClickAsync();
        await Expect(list.GetByText(child)).ToBeVisibleAsync();

        // Back to the root and delete the parent → the delete cascades to the child.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.GetByText(parent)).ToBeVisibleAsync();
        var parentRow = list.Locator(".wb-list-row").Filter(new() { HasText = parent });
        await parentRow.Locator("button").Last.ClickAsync();
        await page.GetByText("Delete").First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(list.GetByText(parent)).Not.ToBeVisibleAsync();

        // Recycle bin tab lists both; restore ONLY the child (its parent stays deleted).
        await page.Locator(".wb-tab[aria-label=\"Recycle bin\"]").First.ClickAsync();
        var bin = page.Locator(".wb-recyclebin");
        var childRow = bin.Locator("tr").Filter(new() { HasText = child });
        await Expect(childRow).ToBeVisibleAsync();
        await childRow.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();
        await Expect(bin.Locator("tr").Filter(new() { HasText = child })).Not.ToBeVisibleAsync();

        // The child is now under an auto-created "Recovered Items" folder at the root — go to Repositories
        // (which refreshes the list) and drill in.
        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.GetByText("Recovered Items")).ToBeVisibleAsync();
        await list.GetByText("Recovered Items").First.DblClickAsync();
        await Expect(list.GetByText(child)).ToBeVisibleAsync();
    }
}
