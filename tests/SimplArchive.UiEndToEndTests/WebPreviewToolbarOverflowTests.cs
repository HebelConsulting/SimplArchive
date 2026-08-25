using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Issue #419: the preview pane's toolbar overflowed its own pane once the pane was narrowed, so its controls were
// drawn ON TOP of the neighbouring chat pane — not merely ugly, since a button sitting over another pane can
// obscure or intercept clicks meant for it. The pane is narrowed both by dragging the chat gutter and by the
// responsive tiers (ADR 0491), so this is reachable without trying.
//
// The desktop had it right already: its preview toolbar is a WrapPanel (PreviewPane.axaml), whose control groups
// wrap onto another line rather than escaping. Per ADR 0511 the web was brought into line with that, so this test
// and the desktop's structure assert the same behaviour.
//
// Geometry, not visibility — the #410 lesson. A control can be visible, clickable, and still in the wrong pane;
// the first attempted fix for #410 did nothing because the CONTAINER was the clipping box, which a visibility
// assertion cannot tell you. So: every toolbar control's right edge must lie within the preview pane's own box.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebPreviewToolbarOverflowTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPreviewToolbarOverflowTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_toolbar_stays_inside_the_preview_pane_when_it_is_narrow()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "ovf-" + Guid.NewGuid().ToString("N")[..8];

        // A .md renders through Gotenberg as a PAGE preview, which is what puts the full toolbar on screen —
        // find box, annotation tools, zoom group (the widest it ever gets), as WebAnnotationTests does.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload
        {
            Name = name + ".md",
            MimeType = "text/markdown",
            Buffer = Encoding.UTF8.GetBytes("# Overflow\n\nBody text.\n"),
        });
        await page.Locator("[data-pane='list']").GetByText(name).First.ClickAsync();
        await Expect(page.Locator(".wb-pv-note-add")).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Squeeze the preview by dragging the chat gutter left (wbLayout.js sizes the chat from the right, so
        // moving the gutter left widens the chat and narrows the preview).
        var gutter = page.Locator("[data-gutter='chat']");
        var gutterBox = await gutter.BoundingBoxAsync();
        Assert.NotNull(gutterBox);
        await page.Mouse.MoveAsync(gutterBox!.X + gutterBox.Width / 2, gutterBox.Y + gutterBox.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(gutterBox.X - 100, gutterBox.Y + gutterBox.Height / 2);
        await page.Mouse.MoveAsync(620, gutterBox.Y + gutterBox.Height / 2); // several steps: the handler is on mousemove
        await page.Mouse.UpAsync();

        var pane = page.Locator("[data-pane='preview']");
        var paneBox = await pane.BoundingBoxAsync();
        Assert.NotNull(paneBox);

        // Precondition, so a failure to narrow can't let the containment assertion pass vacuously: the pane must
        // actually be too small for the toolbar to fit on one line.
        Assert.True(paneBox!.Width < 420, $"expected the preview pane to be narrow, it is {paneBox.Width}px wide");

        var controls = page.Locator(".wb-pv-findbar button, .wb-pv-findbar .mud-input-control");
        var count = await controls.CountAsync();
        Assert.True(count > 5, $"expected the full toolbar, found {count} controls");

        var paneRight = paneBox.X + paneBox.Width;
        for (var i = 0; i < count; i++)
        {
            var control = controls.Nth(i);
            if (!await control.IsVisibleAsync())
            {
                continue;
            }

            var box = await control.BoundingBoxAsync();
            if (box is null)
            {
                continue;
            }

            // 1px for sub-pixel rounding of the border — not a tolerance for a control genuinely hanging out.
            Assert.True(box.X + box.Width <= paneRight + 1,
                $"toolbar control {i} ends at {box.X + box.Width:F0}px, past the preview pane's right edge at {paneRight:F0}px "
                + "— it is drawn over the chat pane (#419)");
            Assert.True(box.X >= paneBox.X - 1,
                $"toolbar control {i} starts at {box.X:F0}px, left of the preview pane's edge at {paneBox.X:F0}px (#419)");
        }
    }
}
