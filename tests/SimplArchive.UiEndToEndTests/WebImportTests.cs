using Microsoft.Playwright;

namespace SimplArchive.UiEndToEndTests;

// The web Repositories ribbon Import… action (ADR "Repository import"): the demo admin exports a repository, then
// imports that same .zip back (grafted under the selected repository) and sees the success confirmation. A full
// browser round-trip over the real stack.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebImportTests
{
    private readonly SelfHostedAppFixture _app;

    public WebImportTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Exports_then_imports_an_archive()
    {
        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();

        // Export → grab the downloaded .zip path.
        await page.Locator(".wb-ribbon [aria-label^=\"Export\"]").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        var download = await page.RunAndWaitForDownloadAsync(async () =>
        {
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Export" }).ClickAsync();
        });
        var zipPath = await download.PathAsync();
        Assert.NotNull(zipPath);

        // Import that archive back (grafted under the still-selected Demo Repository) via the Import dialog.
        await page.Locator(".wb-ribbon [aria-label^=\"Import\"]").First.ClickAsync();
        var importDialog = page.Locator(".mud-dialog");
        await Assertions.Expect(importDialog).ToBeVisibleAsync();
        await importDialog.Locator("#import-dialog-input").SetInputFilesAsync(zipPath!);
        await importDialog.GetByRole(AriaRole.Button, new() { Name = "Import" }).ClickAsync();

        // The success snackbar confirms the import completed.
        await Assertions.Expect(page.GetByText(new System.Text.RegularExpressions.Regex("Imported \"", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .ToBeVisibleAsync(new() { Timeout = 30000 });
    }
}
