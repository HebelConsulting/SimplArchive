using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the tag catalog (ADR "Tag controlled vocabulary"): the demo admin opens the Tags tab and
// creates a coloured catalog tag, which appears in the catalog table.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebTagCatalogTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTagCatalogTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Create_a_catalog_tag_on_the_Tags_tab()
    {
        var tag = $"cat{Guid.NewGuid().ToString("N")[..8]}"; // fresh, lowercase, valid

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Tags\"]").First.ClickAsync();
        await Expect(page.Locator(".wb-tab-active")).ToHaveAttributeAsync("aria-label", "Tags");

        // + opens the create dialog (#530 tranche 6) — the inline form is gone.
        var panel = page.Locator(".wb-tags");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Add tag" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator("input").First.FillAsync(tag);
        await dialog.Locator("input").Nth(1).FillAsync("#2e7d32");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Add tag" }).ClickAsync();

        // The new catalog tag appears as a chip in the table.
        await Expect(panel.GetByText(tag)).ToBeVisibleAsync();
    }
}
