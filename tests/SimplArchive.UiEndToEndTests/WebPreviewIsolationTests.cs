using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Task 3: the Repositories preview pane and the Inbox preview pane share one JS-owned host + _pv* state
// (ADR 0294), cleared on tab switch (ClearPreviewPane). This proves they don't entangle — neither pane ever
// renders the other tab's document content.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebPreviewIsolationTests
{
    private const string InboxMarker = "INBOXMARKERZZZ";
    private const string RepoMarker = "Invoice 2025-001";

    private readonly SelfHostedAppFixture _app;

    public WebPreviewIsolationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Repository_and_inbox_previews_do_not_leak_into_each_other()
    {
        var page = await Ui.LoginAsync(_app);
        var preview = page.Locator(".wb-preview");

        // Repositories: select the seeded document → its content renders in the preview pane.
        await SelectRepoDocumentAsync(page);
        await Expect(preview).ToContainTextAsync(RepoMarker);

        // Inbox: upload a distinctively-worded file, select it → its content renders, and the Repositories
        // document's content must NOT still be showing in the shared pane.
        await page.Locator(".wb-tab").Filter(new LocatorFilterOptions { HasText = "Inbox" }).First.ClickAsync();
        await page.SetInputFilesAsync("#inbox-file-input", new FilePayload
        {
            Name = "inbox-note.txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes($"{InboxMarker} this is the inbox item body"),
        });
        await page.Locator(".wb-list-row").Filter(new() { HasText = "inbox-note" }).First.ClickAsync();
        await Expect(preview).ToContainTextAsync(InboxMarker);
        await Expect(preview).Not.ToContainTextAsync(RepoMarker);

        // Back to Repositories: re-select the document → its content renders, and the inbox item's content must
        // NOT have leaked into this pane.
        await page.Locator(".wb-tab").Filter(new LocatorFilterOptions { HasText = "Repositories" }).First.ClickAsync();
        await SelectRepoDocumentAsync(page);
        await Expect(preview).ToContainTextAsync(RepoMarker);
        await Expect(preview).Not.ToContainTextAsync(InboxMarker);
    }

    private static async Task SelectRepoDocumentAsync(IPage page)
    {
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await list.GetByText("Invoices").First.DblClickAsync();
        await list.GetByText(RepoMarker).First.ClickAsync();
    }
}
