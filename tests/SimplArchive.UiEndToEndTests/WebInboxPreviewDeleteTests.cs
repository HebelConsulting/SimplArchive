using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0265/0279): an inbox item is previewed inline (a .txt renders as text in the inbox preview
// pane), then deleted via the row's ⋮ menu → confirm → it leaves the inbox.
[Collection(UiCollection.Name)]
public class WebInboxPreviewDeleteTests
{
    private readonly SelfHostedAppFixture _app;

    public WebInboxPreviewDeleteTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Inbox_item_previews_inline_then_deletes()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "inboxprev" + Guid.NewGuid().ToString("N")[..8];
        var marker = "brontosaurus" + Guid.NewGuid().ToString("N")[..6];

        // Upload a .txt to the inbox → it appears in the list.
        await page.Locator(".wb-tab").Filter(new() { HasText = "Inbox" }).First.ClickAsync();
        await page.SetInputFilesAsync("#inbox-file-input", new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes(marker),
        });
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();

        // Select it → the inbox preview pane renders the text content inline.
        await row.ClickAsync();
        await Expect(page.Locator(".wb-preview")).ToContainTextAsync(marker);

        // Delete via the row's ⋮ menu → confirm → it leaves the inbox.
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("Delete").First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();
    }
}
