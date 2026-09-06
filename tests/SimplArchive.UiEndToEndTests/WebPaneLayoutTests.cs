using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0224): collapsing a workbench pane via its gutter caret marks it collapsed and persists across
// a page reload (localStorage).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebPaneLayoutTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPaneLayoutTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Collapsing_a_pane_persists_across_a_reload()
    {
        var page = await Ui.LoginAsync(_app);
        await Expect(page.GetByText("Demo Repository")).ToBeVisibleAsync();

        // Collapse the tree pane via its gutter caret.
        await page.Locator("[data-gutter='tree'] .wb-gutter-toggle").ClickAsync();
        await Expect(page.Locator("[data-pane='tree']")).ToHaveAttributeAsync("data-collapsed", "1");

        // Reload → the workbench comes back (silent re-auth) and the pane is still collapsed.
        await page.ReloadAsync();
        await Expect(page.Locator(".wb-tab[aria-label=\"Repositories\"]").First).ToBeVisibleAsync();
        await Expect(page.Locator("[data-pane='tree']")).ToHaveAttributeAsync("data-collapsed", "1");
    }

    [Fact]
    public async Task Collapsing_the_preview_persists_and_hands_the_row_to_the_chat_pane()
    {
        var page = await Ui.LoginAsync(_app);
        await Expect(page.GetByText("Demo Repository")).ToBeVisibleAsync();

        // Collapse the preview via its own gutter caret (it is the grow pane — the annotations/chat pane
        // absorbs the freed width, asserted here as "still visible while the preview is collapsed").
        await page.Locator("[data-gutter='preview'] .wb-gutter-toggle").ClickAsync();
        await Expect(page.Locator("[data-pane='preview']")).ToHaveAttributeAsync("data-collapsed", "1");
        await Expect(page.Locator("[data-pane='chat']")).ToBeVisibleAsync();

        // Reload → still collapsed (persisted), then re-expand so later tests meet a default layout.
        await page.ReloadAsync();
        await Expect(page.Locator(".wb-tab[aria-label=\"Repositories\"]").First).ToBeVisibleAsync();
        await Expect(page.Locator("[data-pane='preview']")).ToHaveAttributeAsync("data-collapsed", "1");
        await page.Locator("[data-gutter='preview'] .wb-gutter-toggle").ClickAsync();
        await Expect(page.Locator("[data-pane='preview']")).Not.ToHaveAttributeAsync("data-collapsed", "1");
    }
}
