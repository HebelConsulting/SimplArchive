using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0290): multi-select 2+ inbox items and "File N items" into a picked folder via the bulk
// filing dialog — all selected items leave the inbox and appear as documents in that folder.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebInboxBulkFilingTests
{
    private readonly SelfHostedAppFixture _app;

    public WebInboxBulkFilingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Bulk_files_multiple_inbox_items_into_a_folder()
    {
        var page = await Ui.LoginAsync(_app);
        var a = "bulk-a-" + Guid.NewGuid().ToString("N")[..8];
        var b = "bulk-b-" + Guid.NewGuid().ToString("N")[..8];

        await page.Locator(".wb-tab[aria-label=\"Inbox\"]").First.ClickAsync();
        await page.SetInputFilesAsync("#inbox-file-input", new[]
        {
            new FilePayload { Name = a + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("a") },
            new FilePayload { Name = b + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("b") },
        });

        var rowA = page.Locator(".wb-list-row").Filter(new() { HasText = a });
        var rowB = page.Locator(".wb-list-row").Filter(new() { HasText = b });
        await Expect(rowA).ToBeVisibleAsync();
        await Expect(rowB).ToBeVisibleAsync();

        // Select both → the "File 2 items" bulk button appears. Ctrl-click, not the checkbox: the checkbox
        // column is the TOUCH affordance and is hidden on a hover-capable pointer device, which is what a
        // Playwright-driven Chrome is. A synthetic click carrying ctrlKey, because Playwright's real-input
        // modifier click does not reach Blazor's MouseEventArgs (the same trick WebBulkActionsTests uses).
        var ctrl = new Dictionary<string, object> { ["ctrlKey"] = true, ["bubbles"] = true };
        await rowA.DispatchEventAsync("click", ctrl);
        await rowB.DispatchEventAsync("click", ctrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "File 2 items" }).ClickAsync();

        // Bulk dialog defaults to folder-pick (nothing selected on Repositories) → pick the repo → File.
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "File", Exact = true }).ClickAsync();

        // Both leave the inbox...
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = a })).Not.ToBeVisibleAsync();
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = b })).Not.ToBeVisibleAsync();

        // ...and both appear as documents in the repository.
        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await Expect(list.GetByText(a)).ToBeVisibleAsync();
        await Expect(list.GetByText(b)).ToBeVisibleAsync();
    }
}
