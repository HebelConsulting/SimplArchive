using System.Globalization;
using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Preview zoom (#357): the preview opens at fit-width (zoom 1); the zoom-in button grows the page past the host so
// the host scrolls horizontally; reset returns to fit. Zoom state lives on the host's data-zoom (set by preview.js;
// pinch / Ctrl-wheel drive the same path). A .md uploads and renders as a converted PDF page, giving a real
// .wb-pv-page to zoom.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebPreviewZoomTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPreviewZoomTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Zoom_in_grows_the_page_and_reset_returns_to_fit()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "zoom-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".md", MimeType = "text/markdown", Buffer = Encoding.UTF8.GetBytes("# Zoom test\n\nA page of body text to render and zoom.\n") });
        await list.GetByText(name).First.ClickAsync();

        var host = page.Locator(".wb-pv-host").First;
        await Expect(page.Locator(".wb-pv-page").First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // Opens at fit-width: zoom 1, page not wider than the host.
        Assert.Equal("1", await host.GetAttributeAsync("data-zoom"));
        Assert.True(await host.EvaluateAsync<bool>("h => h.scrollWidth <= h.clientWidth + 1"), "at fit the host should not scroll horizontally");

        // Zoom in twice → data-zoom rises above fit and the page overflows the host (horizontally scrollable).
        await page.Locator(".wb-pv-zoom-in").First.ClickAsync();
        await page.Locator(".wb-pv-zoom-in").First.ClickAsync();
        var zoom = double.Parse((await host.GetAttributeAsync("data-zoom"))!, CultureInfo.InvariantCulture);
        Assert.True(zoom > 1.4, $"zoom should be >1.4 after two zoom-ins, was {zoom}");
        Assert.True(await host.EvaluateAsync<bool>("h => h.scrollWidth > h.clientWidth + 1"), "zoomed-in host should scroll horizontally");

        // Reset → back to fit.
        await page.Locator(".wb-pv-zoom-reset").First.ClickAsync();
        Assert.Equal("1", await host.GetAttributeAsync("data-zoom"));
        Assert.True(await host.EvaluateAsync<bool>("h => h.scrollWidth <= h.clientWidth + 1"), "after reset the host should not scroll horizontally");
    }
}
