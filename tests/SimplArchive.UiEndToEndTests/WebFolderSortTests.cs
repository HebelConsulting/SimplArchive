using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Per-folder contents sort order (ADR "Per-folder contents sort order"), as it appears after issue #408: the
// order is a ROW in the one detail pane a folder and a document now share, not a pane of its own — so it is
// asserted alongside the folder's system fields, and revealed by the header PENCIL rather than a bottom Edit
// button (issue #407).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebFolderSortTests
{
    private readonly SelfHostedAppFixture _app;

    public WebFolderSortTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Folder_detail_pane_shows_the_contents_sort_control()
    {
        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();

        var detail = page.Locator("[data-pane='index']");
        await Expect(detail.GetByText("Contents sort order")).ToBeVisibleAsync();
        await Expect(detail.GetByText("Document date")).ToBeVisibleAsync(); // the default order

        // The folder gets the DOCUMENT's pane now, so its own metadata is there too — this is the assertion that
        // would have failed before #408, when a folder showed the sort order and nothing else.
        await Expect(detail.GetByText("Demo Repository").First).ToBeVisibleAsync();

        // The header pencil reveals the picker (issue #407 moved the entry point out of the pane's bottom row).
        await detail.GetByRole(AriaRole.Button, new() { Name = "Edit" }).First.ClickAsync();
        await Expect(detail.Locator(".mud-select").First).ToBeVisibleAsync();
    }
}
