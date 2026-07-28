using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0265/0286): upload a file to the inbox, then File it into a repository folder via the filing
// dialog's folder picker — it leaves the inbox and appears as a document in that folder.
[Collection(UiCollection.Name)]
public class WebInboxFilingTests
{
    private readonly SelfHostedAppFixture _app;

    public WebInboxFilingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Inbox_item_can_be_filed_into_a_repository_folder()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "inboxdoc" + Guid.NewGuid().ToString("N")[..8];
        var fileName = name + ".txt";

        // Inbox: upload a file → it appears in the inbox list.
        await page.Locator(".wb-tab").Filter(new() { HasText = "Inbox" }).First.ClickAsync();
        await page.SetInputFilesAsync("#inbox-file-input", new FilePayload
        {
            Name = fileName,
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("filed via the inbox"),
        });
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();

        // File it via the row's ⋮ menu → filing dialog (nothing selected on Repositories → folder-pick mode).
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("File to folder").First.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "File", Exact = true }).ClickAsync();

        // It leaves the inbox...
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();

        // ...and shows up as a document in the target repository (named after the file stem, ADR 0277).
        await page.Locator(".wb-tab").Filter(new() { HasText = "Repositories" }).First.ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(page.Locator("[data-pane='list']").GetByText(name)).ToBeVisibleAsync();
    }
}
