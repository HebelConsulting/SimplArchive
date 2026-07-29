using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The last line of the detail pane shows the document's current (latest confirmed) version number
// (ADR "Mask-pane current-version line"). Uploads a fresh document (version 1) and confirms the line reads it.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebCurrentVersionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebCurrentVersionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Detail_pane_shows_the_current_version_number()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var index = page.Locator("[data-pane='index']");

        var name = "curver-" + Guid.NewGuid().ToString("N")[..8];
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("v1") });

        await Expect(list.GetByText(name)).ToBeVisibleAsync();
        await list.GetByText(name).First.ClickAsync();

        // The current-version line sits in the system-fields table, directly below "Created by".
        var row = index.Locator(".wb-sysfields tr", new() { HasText = "Current version" });
        await Expect(row).ToContainTextAsync("Current version");
        await Expect(row.Locator(".wb-current-version")).ToHaveTextAsync("1");
    }
}
