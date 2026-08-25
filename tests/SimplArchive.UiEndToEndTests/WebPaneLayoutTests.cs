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
}
