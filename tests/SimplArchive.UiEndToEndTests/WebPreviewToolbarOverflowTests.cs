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

        var pane = page.Locator("[data-pane='preview']");
        var before = await pane.BoundingBoxAsync();
        Assert.NotNull(before);

        // Squeeze the preview by dragging the chat gutter left (wbLayout.js sizes the chat from the right, so
        // moving the gutter left widens the chat and narrows the preview).
        //
        // Grabbed ABOVE the gutter's centre, deliberately (#1164). The centre is where the gutter's collapse
        // TOGGLE sits, and wbLayout.js's drag handler opens with
        //     if (btn && (e.target === btn || btn.contains(e.target))) return;
        // — so a press on the toggle starts no drag at all. This test used to grab the exact centre, which made
        // the whole drag a silent no-op: measured on a dev machine, the pane was byte-identical before and
        // after (x=564 w=368 right=932, the gutter's own position). No click fired either, because the mouse
        // moves before the release. So the test squeezed nothing and still passed — see the precondition below
        // for why that was invisible.
        var gutter = page.Locator("[data-gutter='chat']");
        var gutterBox = await gutter.BoundingBoxAsync();
        Assert.NotNull(gutterBox);
        var grabY = gutterBox!.Y + 24; // clear of the centred toggle
        await page.Mouse.MoveAsync(gutterBox.X + gutterBox.Width / 2, grabY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(gutterBox.X - 100, grabY);
        await page.Mouse.MoveAsync(620, grabY); // several steps: the handler is on mousemove
        await page.Mouse.UpAsync();

        var paneBox = await pane.BoundingBoxAsync();
        Assert.NotNull(paneBox);

        // The precondition, so a failure to narrow cannot let the containment assertion pass vacuously. It
        // compares against the width BEFORE the drag rather than against a constant: the old form asserted
        // `Width < 420`, which the DEFAULT layout already satisfies (368px on a 1280px viewport, from
        // DEFAULTS tree 240 + list 300 + chat 340). So the one guard written to catch a failed narrowing was
        // itself satisfied by the unnarrowed pane, and the test reported success while exercising nothing.
        Assert.True(paneBox!.Width < before!.Width - 50,
            $"the drag did not narrow the preview pane — it was {before.Width:F0}px and is {paneBox.Width:F0}px, "
            + "so this test would assert containment against a pane that was never squeezed");
        Assert.True(paneBox.Width < 420, $"expected the preview pane to be narrow, it is {paneBox.Width}px wide");

        // ALL the geometry in ONE layout pass (#1164). Read control-by-control, the pane box and each control
        // box come from SEPARATE round trips, so any re-layout in between — the preview settling, a toolbar
        // group appearing — yields two measurements from two different moments and an overflow that never
        // existed at any single instant. That is the shape of a failure that appears only under load and never
        // in isolation, which is exactly what CI reported here.
        var geometry = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            () => {
                const pane = document.querySelector("[data-pane='preview']").getBoundingClientRect();
                const controls = [...document.querySelectorAll(".wb-pv-findbar button, .wb-pv-findbar .mud-input-control")]
                    .filter(e => e.getClientRects().length > 0)
                    .map(e => { const r = e.getBoundingClientRect(); return { x: r.x, right: r.right, cls: e.className }; });
                return { paneX: pane.x, paneRight: pane.right, controls };
            }
            """);

        var paneLeft = geometry.GetProperty("paneX").GetDouble();
        var paneRight = geometry.GetProperty("paneRight").GetDouble();
        var visible = geometry.GetProperty("controls").EnumerateArray().ToList();
        Assert.True(visible.Count > 5, $"expected the full toolbar, found {visible.Count} visible controls");

        for (var i = 0; i < visible.Count; i++)
        {
            var x = visible[i].GetProperty("x").GetDouble();
            var right = visible[i].GetProperty("right").GetDouble();

            // 1px for sub-pixel rounding of the border — not a tolerance for a control genuinely hanging out.
            Assert.True(right <= paneRight + 1,
                $"toolbar control {i} ends at {right:F0}px, past the preview pane's right edge at {paneRight:F0}px "
                + "— it is drawn over the chat pane (#419)");
            Assert.True(x >= paneLeft - 1,
                $"toolbar control {i} starts at {x:F0}px, left of the preview pane's edge at {paneLeft:F0}px (#419)");
        }
    }
}
