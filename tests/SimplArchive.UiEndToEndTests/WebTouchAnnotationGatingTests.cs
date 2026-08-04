using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Annotation-tool gating on touch (#349): on a touch-only device (no hover, coarse pointer) the annotation
// AUTHORING tools (add-note / highlight / rectangle / arrow / palette / copy / paste / delete) are hidden — the
// drag-to-draw + hover affordances don't work by finger — while existing annotations stay read-only-visible and
// the show/hide toggle remains. IsMobile emulation makes Chromium report (hover: none) and (pointer: coarse); the
// viewport is kept wide so the login helper's app-bar wait still resolves. The desktop authoring path is covered
// by WebAnnotationTests (default, non-touch context).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebTouchAnnotationGatingTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTouchAnnotationGatingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Authoring_tools_are_hidden_on_touch_but_viewing_stays()
    {
        var page = await Ui.LoginAsync(_app, configureContext: o =>
        {
            o.IsMobile = true;
            o.HasTouch = true;
            o.ViewportSize = new ViewportSize { Width = 1280, Height = 800 };
        });
        var name = "touchanno-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".md", MimeType = "text/markdown", Buffer = Encoding.UTF8.GetBytes("# Touch\n\nA page to preview.\n") });
        await list.GetByText(name).First.ClickAsync();

        // The page preview renders and the note show/hide toggle (a viewing control) is available on touch.
        await Expect(page.Locator(".wb-pv-page").First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await Expect(page.Locator(".wb-pv-note-toggle").First).ToBeVisibleAsync();

        // But every authoring control is gated off (not rendered) on the touch-only device.
        await Expect(page.Locator(".wb-pv-note-add")).ToHaveCountAsync(0);
        await Expect(page.Locator(".wb-pv-tool-highlight")).ToHaveCountAsync(0);
        await Expect(page.Locator(".wb-pv-tool-rect")).ToHaveCountAsync(0);
        await Expect(page.Locator(".wb-pv-tool-arrow")).ToHaveCountAsync(0);
        await Expect(page.Locator(".wb-pv-anno-delete")).ToHaveCountAsync(0);
    }
}
