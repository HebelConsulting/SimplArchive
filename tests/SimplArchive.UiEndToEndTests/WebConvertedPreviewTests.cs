using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0232/0235): a non-browser-viewable format is previewed as a server-generated rendition. A .md
// is converted to a PDF (Gotenberg) and rendered, with the "Converted preview" badge marking it as a rendition
// rather than the original.
[Collection(UiCollection.Name)]
public class WebConvertedPreviewTests
{
    private readonly SelfHostedAppFixture _app;

    public WebConvertedPreviewTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Markdown_previews_as_a_converted_pdf_with_a_badge()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "notes-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".md", MimeType = "text/markdown", Buffer = Encoding.UTF8.GetBytes("# Title\n\nHello **Kriens** world.\n") });
        await list.GetByText(name).First.ClickAsync();

        // The preview is a generated rendition → the "Converted preview" badge shows (Gotenberg conversion may
        // take a few seconds).
        await Expect(page.GetByText("Converted preview")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
    }
}
