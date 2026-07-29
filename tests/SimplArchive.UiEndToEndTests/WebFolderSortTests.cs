using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Per-folder contents sort order (ADR "Per-folder contents sort order"): selecting a folder shows a folder
// detail pane with a "Contents sort order" control (defaulting to Document date), and Edit reveals the picker.
[Collection(UiCollection.Name)]
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

        // Edit reveals the sort-order dropdown.
        await detail.GetByRole(AriaRole.Button, new() { Name = "Edit" }).First.ClickAsync();
        await Expect(detail.Locator(".mud-select").First).ToBeVisibleAsync();
    }
}
