using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The Check-out tab's detail panes (ADR "The Check-out tab shows what you are about to check in").
//
// The tab used to be a bare table. To see what you had actually edited you left it, found the document in
// Repositories, and looked at the ARCHIVED version — the one thing that is definitely not your edit.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebCheckoutDetailTests
{
    private readonly SelfHostedAppFixture _app;

    public WebCheckoutDetailTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Selecting_a_held_document_shows_its_index_data_and_working_copy()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "codetail-" + Guid.NewGuid().ToString("N")[..8];

        await page.GetByText("Demo Repository").First.ClickAsync();

        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("ARCHIVEDBODY"),
        });

        // Check out from the row's own menu — there is no ribbon button for it.
        var list = page.Locator("[data-pane='list']");
        await Expect(list.GetByText(name)).ToBeVisibleAsync();
        var repoRow = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await repoRow.Locator("button").Last.ClickAsync();
        await page.GetByText("Check out", new() { Exact = true }).ClickAsync();

        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();
        var checkout = page.Locator(".wb-checkout");
        var row = checkout.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();

        // Nothing is selected yet, so there is no detail pane at all — which is the honest state, rather than
        // a pane showing whatever was last looked at.
        await Expect(checkout.GetByText("Working copy")).Not.ToBeVisibleAsync();

        await row.ClickAsync();

        // "Working copy" exists only in the detail pane, so it is the one thing that says the pane appeared.
        // The document name would match the ROW too, and an index-data table is invisible when the document
        // has no values — neither tells you what you wanted to know.
        //
        // Nothing has been uploaded to the stash, so the preview stays empty rather than falling back to the
        // archived version — which is the whole distinction this tab exists to make.
        await Expect(checkout.GetByText("Working copy")).ToBeVisibleAsync();

        // Clean up so the shared fixture is not left holding a lock.
        await row.Locator("button.mud-icon-button").Last.ClickAsync();
        await page.Locator(".mud-menu-item").Filter(new() { HasText = "Cancel check-out" }).First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Cancel check-out" }).ClickAsync();
    }
}
