using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0216): selecting several files at once in the ribbon Upload creates a separate document per
// file in the folder.
[Collection(UiCollection.Name)]
public class WebMultiUploadTests
{
    private readonly SelfHostedAppFixture _app;

    public WebMultiUploadTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Uploading_multiple_files_creates_a_document_each()
    {
        var page = await Ui.LoginAsync(_app);
        var a = "multi-a-" + Guid.NewGuid().ToString("N")[..8];
        var b = "multi-b-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new[]
        {
            new FilePayload { Name = a + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("a") },
            new FilePayload { Name = b + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("b") },
        });

        await Expect(list.GetByText(a)).ToBeVisibleAsync();
        await Expect(list.GetByText(b)).ToBeVisibleAsync();
    }
}
