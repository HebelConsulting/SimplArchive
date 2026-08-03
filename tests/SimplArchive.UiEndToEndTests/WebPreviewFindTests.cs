using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0294/0282/0269): on a page (PDF) preview, the find bar counts hits with prev/next navigation,
// and clicking a highlighted word copies it to the clipboard. Uses a .md (Gotenberg → PDF with a text layer)
// containing a distinctive word twice.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebPreviewFindTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPreviewFindTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Find_counts_hits_and_clicking_a_hit_copies_the_word()
    {
        var page = await Ui.LoginAsync(_app, new[] { "clipboard-read", "clipboard-write" });
        var name = "findme-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        // "Kriens" appears twice (heading + body).
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".md", MimeType = "text/markdown", Buffer = Encoding.UTF8.GetBytes("# Kriens\n\nHello Kriens world.\n") });
        await list.GetByText(name).First.ClickAsync();

        // Wait for the PDF preview's find bar, then find the word → two hits, prev/next cycles.
        var find = page.Locator("input[placeholder*='Find in document']");
        await Expect(find).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await find.FillAsync("Kriens");
        await Expect(page.GetByText("1 / 2")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

        await page.Locator(".wb-pv-find-next").ClickAsync();
        await Expect(page.GetByText("2 / 2")).ToBeVisibleAsync();

        // Click a highlighted hit → the word is copied to the clipboard.
        await page.Locator(".wb-pv-hit").First.ClickAsync();
        Assert.Equal("Kriens", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
    }
}
