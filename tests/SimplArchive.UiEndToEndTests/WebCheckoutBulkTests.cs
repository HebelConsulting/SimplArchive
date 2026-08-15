using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The Check-out tab's bulk half (#521's last piece): the list multi-selects natively (Ctrl-click), and Cancel
// acts on the whole selection — one confirm naming the count, one "{ok} of {n}" summary, every selected
// check-out released. Check-in shares the same selection + summary path, so releasing N locks through it is
// the UI-level proof of the bulk plumbing.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebCheckoutBulkTests
{
    private readonly SelfHostedAppFixture _app;

    public WebCheckoutBulkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Cancel_releases_every_selected_checkout_with_one_confirm()
    {
        var page = await Ui.LoginAsync(_app);
        var a = "cobulk-a-" + Guid.NewGuid().ToString("N")[..8];
        var b = "cobulk-b-" + Guid.NewGuid().ToString("N")[..8];

        // Upload both throwaway documents first, then check both out — interleaving the two raced the
        // post-check-out list refresh against the second upload's row appearing.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new[]
        {
            new FilePayload { Name = a + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("bulk checkout a") },
            new FilePayload { Name = b + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("bulk checkout b") },
        });
        await Expect(list.GetByText(a)).ToBeVisibleAsync();
        await Expect(list.GetByText(b)).ToBeVisibleAsync();

        foreach (var name in new[] { a, b })
        {
            var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
            await row.Locator("button").Last.ClickAsync();
            await page.GetByText("Check out", new() { Exact = true }).ClickAsync();
            await Expect(list.GetByText($"[Demo Admin] {name}")).ToBeVisibleAsync();
        }

        // Check-out tab: Ctrl-click both rows into the selection. A synthetic click carrying ctrlKey, because
        // Playwright's real-input modifier click does not reach Blazor's MouseEventArgs.
        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();
        var checkout = page.Locator(".wb-checkout");
        var rowA = checkout.Locator(".wb-list-row").Filter(new() { HasText = a });
        var rowB = checkout.Locator(".wb-list-row").Filter(new() { HasText = b });
        await Expect(rowA).ToBeVisibleAsync();
        await Expect(rowB).ToBeVisibleAsync();

        await rowA.ClickAsync();
        var ctrl = new Dictionary<string, object> { ["ctrlKey"] = true, ["bubbles"] = true };
        await rowB.DispatchEventAsync("click", ctrl);

        // Cancel from the toolbar → the bulk confirm names the COUNT → both leave the tab.
        await checkout.Locator("[aria-label=\"Cancel check-out\"]").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.GetByText("2 documents")).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel check-out" }).ClickAsync();

        await Expect(checkout.Locator(".wb-list-row").Filter(new() { HasText = a })).Not.ToBeVisibleAsync();
        await Expect(checkout.Locator(".wb-list-row").Filter(new() { HasText = b })).Not.ToBeVisibleAsync();

        // And the repository rows lose their lock prefixes — the releases really happened server-side.
        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await Expect(list.GetByText($"[Demo Admin] {a}")).Not.ToBeVisibleAsync();
        await Expect(list.GetByText(a)).ToBeVisibleAsync();
    }
}
