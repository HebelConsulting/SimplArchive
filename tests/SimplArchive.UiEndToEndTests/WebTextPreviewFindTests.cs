using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Find in a TEXT preview (#1063): the findbar's search field must exist for kind=text (it was pages-only,
// so searching the aerodrome list read as "not found" for content the rendition demonstrably contained),
// count the matches, and highlight the active one.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebTextPreviewFindTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTextPreviewFindTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Find_in_a_text_preview_counts_and_highlights_matches()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "notes-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("alpha LSPG bravo\nlspg charlie\ndelta LSPG"),
        });
        await list.GetByText(name).First.ClickAsync();

        var preview = page.Locator(".wb-preview");
        await Expect(preview).ToContainTextAsync("alpha LSPG bravo");

        // The find field exists for a text preview and counts case-insensitively.
        var find = preview.Locator(".wb-pv-findgrp input");
        await find.FillAsync("lspg");
        await Expect(preview.Locator(".wb-pv-findgrp")).ToContainTextAsync("1 / 3");
        await Expect(preview.Locator("mark")).ToHaveCountAsync(3);

        // Next advances the active highlight.
        await preview.Locator(".wb-pv-find-next").ClickAsync();
        await Expect(preview.Locator(".wb-pv-findgrp")).ToContainTextAsync("2 / 3");
    }

    [Fact]
    public async Task Line_numbers_are_always_on_and_the_wrap_toggle_governs_overflow()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "log-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload
        {
            Name = name + ".log",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("first line\n" + new string('x', 400) + "\nthird line"),
        });
        await list.GetByText(name).First.ClickAsync();

        var preview = page.Locator(".wb-preview");
        await Expect(preview).ToContainTextAsync("first line");

        // Line numbers: always on (#1317), one per LOGICAL line, and unselectable — a .log is read to be
        // copied out of, so the numbers must never ride along with a selection.
        var numbers = preview.Locator(".wb-pv-linenum");
        await Expect(numbers).ToHaveCountAsync(3);
        await Expect(numbers.First).ToHaveTextAsync("1");
        Assert.Equal("none", await numbers.First.EvaluateAsync<string>("e => getComputedStyle(e).userSelect"));

        // Wrap defaults ON: the 400-character line folds, so nothing overflows sideways.
        var text = preview.Locator(".wb-pv-text");
        Assert.False(await text.EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth + 1"));

        // The toggle turns wrapping off, and the horizontal scrollbar FOLLOWS from that (one control, the
        // scrollbar is its consequence — #1317's decided shape).
        await preview.Locator(".wb-pv-wrap-btn").ClickAsync();
        await Expect(preview.Locator("pre").Nth(1)).ToHaveCSSAsync("white-space", "pre");
        Assert.True(await text.EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth + 1"));

        // And back on.
        await preview.Locator(".wb-pv-wrap-btn").ClickAsync();
        await Expect(preview.Locator("pre").Nth(1)).ToHaveCSSAsync("white-space", "pre-wrap");
    }
}
