using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0234): a .json is previewed as re-indented text, rendered in-process (no Gotenberg) — the
// json/xml rendition family.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebJsonPreviewTests
{
    private readonly SelfHostedAppFixture _app;

    public WebJsonPreviewTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Json_previews_as_reindented_text()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "data-" + Guid.NewGuid().ToString("N")[..8];
        var marker = "zzyzx" + Guid.NewGuid().ToString("N")[..6];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        // Single-line source; the rendition re-indents it.
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".json", MimeType = "application/json", Buffer = Encoding.UTF8.GetBytes($"{{\"marker\":\"{marker}\",\"nested\":{{\"a\":1}}}}") });
        await list.GetByText(name).First.ClickAsync();

        var preview = page.Locator(".wb-preview");
        await Expect(preview).ToContainTextAsync(marker);
        await Expect(preview).ToContainTextAsync("nested"); // re-indented object shows the keys
    }
}
