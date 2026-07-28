using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "Document annotations"): on a page (PDF) preview, the user adds a sticky note by clicking the
// Add-note button then a spot on the page, fills the dialog, and a coloured marker appears; clicking the marker
// and choosing Delete removes it. Uses a .md (Gotenberg → PDF page preview), like WebPreviewFindTests.
[Collection(UiCollection.Name)]
public class WebAnnotationTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAnnotationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Place_a_sticky_note_then_delete_it()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "note-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".md", MimeType = "text/markdown", Buffer = Encoding.UTF8.GetBytes("# Notes page\n\nSome body text.\n") });
        await list.GetByText(name).First.ClickAsync();

        // Wait for the page (PDF) preview to render, then the note controls appear.
        var addButton = page.Locator(".wb-pv-note-add");
        await Expect(addButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // Enter placement mode, then click a spot on the page overlay to drop the note.
        await addButton.ClickAsync();
        await page.Locator(".wb-pv-overlay").First.ClickAsync(new LocatorClickOptions { Position = new Position { X = 60, Y = 60 } });

        // The dialog opens — type the note text and save.
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("textarea").First.FillAsync("Please double-check");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        // The note renders as a box showing its text (ADR "Post-it note boxes" web parity).
        await Expect(page.Locator(".wb-pv-note")).ToHaveCountAsync(1);
        await Expect(page.Locator(".wb-pv-note")).ToContainTextAsync("Please double-check");

        // Drag the marker (the author can reposition it): the server accepts the move (PUT 200) and the
        // re-rendered marker sits further right, at the persisted position.
        var marker = page.Locator(".wb-pv-note").First;
        var box = await marker.BoundingBoxAsync();
        Assert.NotNull(box);
        var placedLeft = await marker.EvaluateAsync<double>("m => parseFloat(m.style.left)");
        var cx = box!.X + box.Width / 2;
        var cy = box.Y + box.Height / 2;
        await page.RunAndWaitForResponseAsync(async () =>
        {
            await page.Mouse.MoveAsync(cx, cy);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(cx + 90, cy + 40, new MouseMoveOptions { Steps = 8 });
            await page.Mouse.UpAsync();
        }, r => r.Request.Method == "PUT" && r.Url.Contains("/annotations/") && r.Status == 200);

        await page.WaitForFunctionAsync(
            "prev => { const m = document.querySelector('.wb-pv-note'); return m && parseFloat(m.style.left) > prev + 3; }",
            placedLeft);

        // Resize the note taller via its corner grip (both dimensions): the box grows past one line and the new
        // size persists (PUT 200).
        var heightBefore = await page.Locator(".wb-pv-note").First.EvaluateAsync<double>("m => m.getBoundingClientRect().height");
        var gcenter = await StableCenterAsync(page, ".wb-pv-note-grip");   // survives the drag re-render
        await page.RunAndWaitForResponseAsync(async () =>
        {
            await page.Mouse.MoveAsync(gcenter.X, gcenter.Y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(gcenter.X + 30, gcenter.Y + 70, new MouseMoveOptions { Steps = 8 });
            await page.Mouse.UpAsync();
        }, r => r.Request.Method == "PUT" && r.Url.Contains("/annotations/") && r.Status == 200);
        await page.WaitForFunctionAsync(
            "prev => { const m = document.querySelector('.wb-pv-note'); return m && m.getBoundingClientRect().height > prev + 20; }",
            heightBefore);

        // Double-click the note box → dialog → Delete → the box is gone (single-click now selects, ADR
        // "Annotation multi-select" web parity — editing is a double-click).
        await page.Locator(".wb-pv-note").First.DblClickAsync();
        var editDialog = page.Locator(".mud-dialog");
        await Expect(editDialog).ToBeVisibleAsync();
        await editDialog.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
        await Expect(page.Locator(".wb-pv-note")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Multi_select_and_delete_annotations()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "multi-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".md", MimeType = "text/markdown", Buffer = Encoding.UTF8.GetBytes("# Multi page\n\nSome body text.\n") });
        await list.GetByText(name).First.ClickAsync();

        var addButton = page.Locator(".wb-pv-note-add");
        await Expect(addButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        var overlay = page.Locator(".wb-pv-overlay").First;

        // Place two notes at distinct spots.
        async Task PlaceNote(int x, int y, string text)
        {
            await addButton.ClickAsync();
            await overlay.ClickAsync(new LocatorClickOptions { Position = new Position { X = x, Y = y } });
            var d = page.Locator(".mud-dialog");
            await Expect(d).ToBeVisibleAsync();
            await d.Locator("textarea").First.FillAsync(text);
            await d.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Expect(d).ToBeHiddenAsync();
        }
        await PlaceNote(50, 40, "first");
        await PlaceNote(50, 120, "second");
        await Expect(page.Locator(".wb-pv-note")).ToHaveCountAsync(2);

        // Select the first (plain click) then add the second (Ctrl-click) → both are outlined.
        await page.Locator(".wb-pv-note", new() { HasTextString = "first" }).ClickAsync();
        await Expect(page.Locator(".wb-pv-note.wb-pv-selected")).ToHaveCountAsync(1);
        await page.Locator(".wb-pv-note", new() { HasTextString = "second" }).ClickAsync(new LocatorClickOptions { Modifiers = new[] { KeyboardModifier.Control } });
        await Expect(page.Locator(".wb-pv-note.wb-pv-selected")).ToHaveCountAsync(2);

        // Delete the selection via the toolbar → both notes are gone.
        await page.Locator(".wb-pv-anno-delete").ClickAsync();
        await Expect(page.Locator(".wb-pv-note")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Draw_a_highlight_markup_shape()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "mk-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".md", MimeType = "text/markdown", Buffer = Encoding.UTF8.GetBytes("# Markup page\n\nSome body text.\n") });
        await list.GetByText(name).First.ClickAsync();

        // The Highlight tool appears once the page preview renders.
        var tool = page.Locator(".wb-pv-tool-highlight");
        await Expect(tool).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await tool.ClickAsync();

        // Drag a box on the page overlay → the server creates a highlight (POST) → the reload draws a .wb-pv-hl.
        var overlay = page.Locator(".wb-pv-overlay").First;
        var box = await overlay.BoundingBoxAsync();
        Assert.NotNull(box);
        await page.RunAndWaitForResponseAsync(async () =>
        {
            await page.Mouse.MoveAsync(box!.X + 40, box.Y + 40);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(box.X + 150, box.Y + 90, new MouseMoveOptions { Steps = 8 });
            await page.Mouse.UpAsync();
        }, r => r.Request.Method == "POST" && r.Url.Contains("/annotations") && r.Status == 201);

        await Expect(page.Locator(".wb-pv-hl")).ToHaveCountAsync(1);

        // The tool auto-deactivates after one draw (ADR "Draw-tool behaviour"), so a click on the highlight now
        // selects it (no manual toggle-off needed); recolour it via the toolbar palette (ADR "Highlighting
        // redesign" — a shape has no edit dialog; the palette recolours the selection).
        await page.Locator(".wb-pv-hl").First.ClickAsync();       // single-click selects the shape
        await Expect(page.Locator(".wb-pv-hl.wb-pv-selected")).ToHaveCountAsync(1);
        // Pick a different palette swatch (#4FC3F7, index 2) → the server recolours the selection (PUT 200).
        await page.RunAndWaitForResponseAsync(async () =>
        {
            await page.Locator(".wb-pv-swatch").Nth(2).ClickAsync();
        }, r => r.Request.Method == "PUT" && r.Url.Contains("/annotations/") && r.Status == 200);
        await Expect(page.Locator(".wb-pv-hl")).ToHaveCountAsync(1);

        // Move the highlight by dragging its body (ADR "Highlighting redesign" — shapes are movable): the new
        // position persists (PUT 200) and the shape sits further right. Each mutation triggers a follow-up reload
        // (a GET that rebuilds the DOM), so wait for network-idle before measuring the (re-rendered) element.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var hbox = await StableCenterAsync(page, ".wb-pv-hl");
        var leftBefore = await page.Locator(".wb-pv-hl").First.EvaluateAsync<double>("s => parseFloat(s.style.left)");
        await page.RunAndWaitForResponseAsync(async () =>
        {
            await page.Mouse.MoveAsync(hbox.X, hbox.Y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(hbox.X + 50, hbox.Y + 20, new MouseMoveOptions { Steps = 8 });
            await page.Mouse.UpAsync();
        }, r => r.Request.Method == "PUT" && r.Url.Contains("/annotations/") && r.Status == 200);
        await page.WaitForFunctionAsync("prev => { const s = document.querySelector('.wb-pv-hl'); return s && parseFloat(s.style.left) > prev + 2; }", leftBefore);

        // Resize the highlight via its corner grip (shapes are resizable): the width grows and persists (PUT 200).
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var widthBefore = await page.Locator(".wb-pv-hl").First.EvaluateAsync<double>("s => s.getBoundingClientRect().width");
        var gcenter = await StableCenterAsync(page, ".wb-pv-shape-grip");
        await page.RunAndWaitForResponseAsync(async () =>
        {
            await page.Mouse.MoveAsync(gcenter.X, gcenter.Y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(gcenter.X + 70, gcenter.Y + 25, new MouseMoveOptions { Steps = 8 });
            await page.Mouse.UpAsync();
        }, r => r.Request.Method == "PUT" && r.Url.Contains("/annotations/") && r.Status == 200);
        await page.WaitForFunctionAsync("prev => { const s = document.querySelector('.wb-pv-hl'); return s && s.getBoundingClientRect().width > prev + 20; }", widthBefore);
    }

    // The viewport-centre of an element, read via a polling WaitForFunction so it survives the Blazor re-render
    // that each annotation mutation triggers (a detached element makes BoundingBoxAsync return null).
    private static async Task<(float X, float Y)> StableCenterAsync(IPage page, string selector)
    {
        var handle = await page.WaitForFunctionAsync(
            $"() => {{ const el = document.querySelector('{selector}'); if (!el) return null; const r = el.getBoundingClientRect(); return (r.width > 1 && r.height > 0) ? {{ x: r.x + r.width / 2, y: r.y + r.height / 2 }} : null; }}");
        var v = await handle.JsonValueAsync<System.Text.Json.JsonElement>();
        return ((float)v.GetProperty("x").GetDouble(), (float)v.GetProperty("y").GetDouble());
    }
}
