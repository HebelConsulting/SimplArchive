using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0265/0279): an intray item is previewed inline (a .txt renders as text in the intray preview
// pane), then deleted via the row's ⋮ menu → confirm → it leaves the intray.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebIntrayPreviewDeleteTests
{
    private readonly SelfHostedAppFixture _app;

    public WebIntrayPreviewDeleteTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Intray_item_previews_inline_then_deletes()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "intrayprev" + Guid.NewGuid().ToString("N")[..8];
        var marker = "brontosaurus" + Guid.NewGuid().ToString("N")[..6];

        // Upload a .txt to the intray → it appears in the list.
        await page.Locator(".wb-tab[aria-label=\"Intray\"]").First.ClickAsync();
        await page.SetInputFilesAsync("#intray-file-input", new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes(marker),
        });
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();

        // Select it → the intray preview pane renders the text content inline.
        await row.ClickAsync();
        await Expect(page.Locator(".wb-preview")).ToContainTextAsync(marker);

        // Delete via the row's ⋮ menu → confirm → it leaves the intray.
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("Delete").First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();
    }
}
