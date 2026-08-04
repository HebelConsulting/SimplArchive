using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0286/0287/0528): with a document selected on Repositories, the inbox filing dialog can file an
// item as a NEW VERSION of it — the item leaves the inbox, and the filing comment is now the new version's comment
// (shown in the versions dialog), no longer a chat/feed post.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebInboxVersionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebInboxVersionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Filing_an_inbox_item_as_a_new_version_writes_the_version_comment_not_a_feed_post()
    {
        var page = await Ui.LoginAsync(_app);
        var doc = "verdoc-" + Guid.NewGuid().ToString("N")[..8];
        var inboxItem = "newver-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        // Upload a document and select it (so the filing dialog offers file-as-version).
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = doc + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("v1") });
        await list.GetByText(doc).First.ClickAsync();

        // Upload an inbox item and file it as a new version of the selected document.
        await page.Locator(".wb-tab[aria-label=\"Inbox\"]").First.ClickAsync();
        await page.SetInputFilesAsync("#inbox-file-input", new FilePayload { Name = inboxItem + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("v2") });

        await page.Locator(".wb-list-row").Filter(new() { HasText = inboxItem }).Locator("button").Last.ClickAsync();
        await page.GetByText("File to folder").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("File as a new version").First.ClickAsync();
        // Type a filing comment — it becomes the new version's comment.
        var commentText = "checked-in via inbox " + Guid.NewGuid().ToString("N")[..6];
        var commentField = dialog.Locator(".mud-input-control input, .mud-input-control textarea").Last;
        await commentField.FillAsync(commentText);
        await commentField.BlurAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "File" }).ClickAsync();

        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = inboxItem })).Not.ToBeVisibleAsync();

        // Back on Repositories, the comment is the NEW VERSION's comment (in the versions dialog) — NOT a feed post.
        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await list.GetByText(doc).First.ClickAsync();

        await page.Locator(".wb-ribbon [aria-label=\"Versions\"]").First.ClickAsync();
        await Expect(page.Locator(".mud-dialog")).ToContainTextAsync(commentText);
        // The filing comment did not go to the chat feed.
        await Expect(page.Locator(".mud-dialog")).Not.ToContainTextAsync("Filed a new document");
    }
}
