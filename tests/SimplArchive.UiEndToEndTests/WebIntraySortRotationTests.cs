using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web sort dialog's rotation half (#522): each tile carries a turn-left and a turn-right button in its
// lower part, the thumbnail turns IMMEDIATELY (or the user cannot tell the click registered), and Apply writes
// the whole arrangement in one request. Server-side correctness of the written rotation is
// IntrayPageOperationsTests' job — this proves the affordance and the immediate feedback.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebIntraySortRotationTests
{
    private readonly SelfHostedAppFixture _app;

    public WebIntraySortRotationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Rotating_a_tile_turns_its_thumbnail_and_apply_succeeds()
    {
        var page = await Ui.LoginAsync(_app);

        // Stage the checked-in 7-page sample batch — the app itself serves it, and its page 4 is the
        // deliberately upside-down one this feature exists for.
        var name = "rot-" + Guid.NewGuid().ToString("N")[..8];
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync($"{_app.BaseUrl}/download/samples/SimplArchive-Patch3-Sample-Batch.pdf");

        await page.Locator(".wb-tab[aria-label=\"Intray\"]").First.ClickAsync();
        await page.SetInputFilesAsync("#intray-file-input", new FilePayload
        {
            Name = name + ".pdf",
            MimeType = "application/pdf",
            Buffer = bytes,
        });
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        // The Sort button appears once the server said the item can be sorted (ADR 0554).
        var sort = page.Locator(".wb-search-bar [aria-label=\"Rotate/Sort\"], .wb-search-bar [title=\"Rotate/Sort\"]").First;
        await Expect(sort).ToBeVisibleAsync(new() { Timeout = 15000 });
        await sort.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15000 });

        // Turn page 4 a quarter right: the thumbnail must show the turn immediately.
        var tile = dialog.Locator("[data-page='4']");
        await Expect(tile).ToBeVisibleAsync();
        await dialog.Locator("[data-rotate-right='4']").ClickAsync();
        await Expect(tile.Locator("img")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("rotate\\(90deg\\)"));

        // And a left turn from there lands back at 0 — the state machine, driven the way a user drives it.
        await dialog.Locator("[data-rotate-left='4']").ClickAsync();
        await Expect(tile.Locator("img")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("rotate\\(0deg\\)"));

        // Turn it once more and apply: the single request carries order + turns, and the dialog closes clean.
        await dialog.Locator("[data-rotate-right='4']").ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Apply order" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).ToBeVisibleAsync();
    }
}
