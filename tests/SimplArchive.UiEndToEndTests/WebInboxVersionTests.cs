using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0286/0287): with a document selected on Repositories, the inbox filing dialog can file an item
// as a NEW VERSION of it — the item leaves the inbox and a filing comment is posted on the target document.
[Collection(UiCollection.Name)]
public class WebInboxVersionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebInboxVersionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Filing_an_inbox_item_as_a_new_version_posts_a_comment_on_the_document()
    {
        var page = await Ui.LoginAsync(_app);
        var doc = "verdoc-" + Guid.NewGuid().ToString("N")[..8];
        var inboxItem = "newver-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        // Upload a document and select it (so the filing dialog offers file-as-version).
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = doc + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("v1") });
        await list.GetByText(doc).First.ClickAsync();

        // Upload an inbox item and file it as a new version of the selected document.
        await page.Locator(".wb-tab").Filter(new() { HasText = "Inbox" }).First.ClickAsync();
        await page.SetInputFilesAsync("#inbox-file-input", new FilePayload { Name = inboxItem + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("v2") });

        await page.Locator(".wb-list-row").Filter(new() { HasText = inboxItem }).Locator("button").Last.ClickAsync();
        await page.GetByText("File to folder").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("File as a new version").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "File" }).ClickAsync();

        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = inboxItem })).Not.ToBeVisibleAsync();

        // Back on Repositories, the target document's feed shows the filing comment.
        await page.Locator(".wb-tab").Filter(new() { HasText = "Repositories" }).First.ClickAsync();
        await list.GetByText(doc).First.ClickAsync();
        await Expect(page.Locator(".wb-chat")).ToContainTextAsync("Filed a new document");
    }
}
