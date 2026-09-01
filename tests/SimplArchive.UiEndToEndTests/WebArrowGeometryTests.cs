using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The arrow markup annotation is drawn to the DESKTOP's geometry (ADR 0511 makes the desktop canonical):
// an 11-page-pixel head and a 2-pixel tail, exactly what HighlightOverlayDrawing.DrawShape uses.
//
// WHY THIS TEST EXISTS (#921). Both clients build the arrowhead with the same maths, but the web expressed it
// in the SVG's 0..100 viewBox — so the head was a PERCENTAGE of the page (3.5%, i.e. ~28px on an 800px page
// against the desktop's 11px) while the tail was pinned at a non-scaling 0.8px hairline. The two halves scaled
// in opposite directions and the arrow came out roughly six times too head-heavy. Reported by eye, from the
// running app; nothing failed, because no test looked at the shape.
//
// A pixel-size assertion is what catches that class of defect: the previous geometry passes any test that only
// asks whether an arrow EXISTS.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebArrowGeometryTests
{
    private readonly SelfHostedAppFixture _app;

    public WebArrowGeometryTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task An_arrow_has_the_desktops_head_size_and_tail_width()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "arrow-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload
        {
            Name = name + ".md",
            MimeType = "text/markdown",
            Buffer = Encoding.UTF8.GetBytes("# Arrow page\n\nSome body text.\n"),
        });
        await list.GetByText(name).First.ClickAsync();

        var arrowTool = page.Locator(".wb-pv-tool-arrow");
        await Expect(arrowTool).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await arrowTool.ClickAsync();

        // Drag a horizontal arrow across the page.
        var overlay = page.Locator(".wb-pv-overlay").First;
        var box = await overlay.BoundingBoxAsync();
        Assert.NotNull(box);
        await page.Mouse.MoveAsync(box!.X + 40, box.Y + 80);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + 240, box.Y + 80, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.UpAsync();

        await Expect(page.Locator(".wb-pv-arrow")).ToHaveCountAsync(1);

        // THE HEAD MUST NOT SCALE WITH THE PAGE — that is the actual invariant, and a single measurement cannot
        // see it: on this preview's page width, 3.5% happens to land near 11px, so the broken geometry and the
        // correct one AGREE here. (Found the honest way: the first version of this test asserted one size, and
        // it still passed with the defect restored.) Zooming in doubles the page; an 11px head stays 11px, while
        // a head sized in viewBox units grows with it.
        var headBefore = await page.Locator(".wb-pv-arrow polygon").First
            .EvaluateAsync<double>("p => p.getBoundingClientRect().width");
        var pageBefore = await page.Locator(".wb-pv-overlay").First
            .EvaluateAsync<double>("o => o.getBoundingClientRect().width");

        await page.Locator(".wb-pv-zoom-in").First.ClickAsync();
        await page.Locator(".wb-pv-zoom-in").First.ClickAsync();
        await page.WaitForTimeoutAsync(400);

        var headAfter = await page.Locator(".wb-pv-arrow polygon").First
            .EvaluateAsync<double>("p => p.getBoundingClientRect().width");
        var pageAfter = await page.Locator(".wb-pv-overlay").First
            .EvaluateAsync<double>("o => o.getBoundingClientRect().width");

        Assert.True(pageAfter > pageBefore * 1.2,
            $"the page did not grow on zoom ({pageBefore:F0} -> {pageAfter:F0}px), so this test cannot tell a "
            + "page-pixel head from a viewBox-unit one");
        Assert.True(headAfter < headBefore * 1.25,
            $"the arrowhead grew with the page ({headBefore:F1} -> {headAfter:F1}px while the page went "
            + $"{pageBefore:F0} -> {pageAfter:F0}px): it is sized in viewBox units, i.e. as a PERCENTAGE of the "
            + "page, instead of the desktop's fixed 11 page pixels (#921)");

        // The tail: the desktop's 2px pen, not the 0.8px hairline.
        var stroke = await page.Locator(".wb-pv-arrow line").First
            .EvaluateAsync<double>("l => parseFloat(getComputedStyle(l).strokeWidth)");
        Assert.True(stroke >= 1.5,
            $"the arrow's tail is {stroke}px; the desktop draws 2px, and a hairline tail under a full-size head is "
            + "what made the web arrow look head-heavy (#921)");
    }
}
