using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0290): checkbox-select 2+ inbox items and "File N items" into a picked folder via the bulk
// filing dialog — all selected items leave the inbox and appear as documents in that folder.
[Collection(UiCollection.Name)]
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

        await page.Locator(".wb-tab").Filter(new() { HasText = "Inbox" }).First.ClickAsync();
        await page.SetInputFilesAsync("#inbox-file-input", new[]
        {
            new FilePayload { Name = a + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("a") },
            new FilePayload { Name = b + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("b") },
        });

        var rowA = page.Locator(".wb-list-row").Filter(new() { HasText = a });
        var rowB = page.Locator(".wb-list-row").Filter(new() { HasText = b });
        await Expect(rowA).ToBeVisibleAsync();
        await Expect(rowB).ToBeVisibleAsync();

        // Check both → the "File 2 items" bulk button appears.
        await rowA.Locator(".mud-checkbox").First.ClickAsync();
        await rowB.Locator(".mud-checkbox").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "File 2 items" }).ClickAsync();

        // Bulk dialog defaults to folder-pick (nothing selected on Repositories) → pick the repo → File.
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "File", Exact = true }).ClickAsync();

        // Both leave the inbox...
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = a })).Not.ToBeVisibleAsync();
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = b })).Not.ToBeVisibleAsync();

        // ...and both appear as documents in the repository.
        await page.Locator(".wb-tab").Filter(new() { HasText = "Repositories" }).First.ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await Expect(list.GetByText(a)).ToBeVisibleAsync();
        await Expect(list.GetByText(b)).ToBeVisibleAsync();
    }
}
