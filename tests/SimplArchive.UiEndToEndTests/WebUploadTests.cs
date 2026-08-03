using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0216/0292): the ribbon Upload button uploads a file straight into the selected folder (browser
// → presigned PUT to MinIO → finalize) and the new document appears in the contents list, previewable. Distinct
// from the inbox-filing path already covered.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebUploadTests
{
    private readonly SelfHostedAppFixture _app;

    public WebUploadTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Uploading_a_file_creates_a_document_in_the_folder()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "uploaded-" + Guid.NewGuid().ToString("N")[..8];

        // Select the repository so Upload is enabled and targets it.
        await page.GetByText("Demo Repository").First.ClickAsync();

        // Ribbon Upload → the browser file chooser (a hidden input, ADR 0216).
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("uploaded via the ribbon ZZZ"),
        });

        // The new document appears (named after the file stem, ADR 0277/0292) and previews its content.
        var list = page.Locator("[data-pane='list']");
        await Expect(list.GetByText(name)).ToBeVisibleAsync();
        await list.GetByText(name).First.ClickAsync();
        await Expect(page.Locator(".wb-preview")).ToContainTextAsync("uploaded via the ribbon ZZZ");
    }
}
