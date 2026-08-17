using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A web UI flow (ADR "Web check-out"): the demo admin checks out a document from the Repositories row menu, sees
// it prefixed with "[name]" + a lock icon, finds it on the Check-out tab, and cancels the check-out.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebCheckoutTests
{
    private readonly SelfHostedAppFixture _app;

    public WebCheckoutTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Check_out_shows_on_the_tab_and_prefixes_the_row_then_cancels()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "co-" + Guid.NewGuid().ToString("N")[..8];

        // Upload a throwaway document into the demo repository.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("checkout web") });
        await Expect(list.GetByText(name)).ToBeVisibleAsync();

        // Row ⋮ menu → Check out.
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("Check out", new() { Exact = true }).ClickAsync();

        // The row is now prefixed with the holder's display name (the demo admin).
        await Expect(list.GetByText($"[Demo Admin] {name}")).ToBeVisibleAsync();

        // The Check-out tab lists it.
        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();
        var checkout = page.Locator(".wb-checkout");
        await Expect(checkout).ToBeVisibleAsync();
        // Rows are list rows, not table rows: the tab now has the Intray's two-pane shape, so the list shares
        // half the width with the detail panes and a wide table no longer fits.
        var checkoutRow = checkout.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(checkoutRow).ToBeVisibleAsync();

        // Cancel the check-out → confirm → it leaves the tab. The row actions live in a per-row menu.
        await checkoutRow.Locator("button.mud-icon-button").Last.ClickAsync();
        // MudMenuItem renders as .mud-menu-item, not an ARIA menuitem — matching by role finds nothing.
        await page.Locator(".mud-menu-item").Filter(new() { HasText = "Cancel check-out" }).First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Cancel check-out" }).ClickAsync();
        await Expect(checkout.Locator(".wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();
    }
}
