using System.IO.Compression;
using Microsoft.Playwright;

namespace SimplArchive.UiEndToEndTests;

// The web Repositories ribbon Export… action (ADR "Repository export"): the demo admin (a tenant admin) selects
// a repository, opens the export dialog, and downloads a .zip whose archive carries the manifest.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebExportTests
{
    private readonly SelfHostedAppFixture _app;

    public WebExportTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Exports_the_selected_repository_to_a_zip()
    {
        var page = await Ui.LoginAsync(_app);

        // Select the seeded repository in the tree so the ribbon Export… enables.
        await page.GetByText("Demo Repository").First.ClickAsync();

        // Ribbon Export… → the filter dialog (defaults: all versions) → the dialog Export triggers the download.
        await page.Locator(".wb-ribbon").GetByText("Export").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Assertions.Expect(dialog).ToBeVisibleAsync();

        var download = await page.RunAndWaitForDownloadAsync(async () =>
        {
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Export" }).ClickAsync();
        });

        Assert.EndsWith(".zip", download.SuggestedFilename);
        Assert.Contains("Demo Repository", download.SuggestedFilename);

        var path = await download.PathAsync();
        Assert.NotNull(path);
        using var archive = ZipFile.OpenRead(path!);
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.Contains(archive.Entries, e => e.FullName.StartsWith("blobs/"));
    }
}
