using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0196): a leaf document round-trips through the recycle bin — upload it, Delete it (⋮ menu →
// confirm), then Restore it from the repository Recycle bin. The lifecycle test covers a folder; this covers
// a document.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebRecycleBinDocumentTests
{
    private readonly SelfHostedAppFixture _app;

    public WebRecycleBinDocumentTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Document_can_be_deleted_and_restored_from_the_recycle_bin()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "recyclebin-doc-" + Guid.NewGuid().ToString("N")[..8];

        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");

        // Upload a document via the ribbon.
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("bin me") });
        await Expect(list.GetByText(name)).ToBeVisibleAsync();

        // Delete via the row's ⋮ menu → confirm.
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("Delete").First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(list.GetByText(name)).Not.ToBeVisibleAsync();

        // Recycle bin tab → the deleted document is listed → Restore removes it from the bin.
        await page.Locator(".wb-tab[aria-label=\"Recycle bin\"]").First.ClickAsync();
        var bin = page.Locator(".wb-recyclebin");
        var docRow = bin.Locator("tr").Filter(new() { HasText = name });
        await Expect(docRow).ToBeVisibleAsync();
        await docRow.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();
        await Expect(bin.Locator("tr").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();
    }
}
