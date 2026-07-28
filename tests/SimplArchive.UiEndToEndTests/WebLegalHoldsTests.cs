using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "Legal hold & retention enforcement"): the demo admin — granted CanLegalHold by the demo seed
// — sees the Legal Holds tab, creates a new matter, and releases it. A standalone empty matter is used (no
// shared document is placed on hold), so nothing else in the suite is affected.
[Collection(UiCollection.Name)]
public class WebLegalHoldsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebLegalHoldsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Create_and_release_a_legal_hold()
    {
        var page = await Ui.LoginAsync(_app);
        var matter = $"Matter {Guid.NewGuid().ToString("N")[..8]}";

        // The tab is gated on whoami.canLegalHold — visible for the demo admin.
        await page.Locator(".wb-tab").Filter(new() { HasText = "Legal holds" }).First.ClickAsync();
        await Expect(page.Locator(".wb-legalholds")).ToBeVisibleAsync();

        // Create a new matter via the dialog.
        await page.GetByRole(AriaRole.Button, new() { Name = "New hold" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        // The matter-name field is the dialog's first text input; commit on blur (MudTextField commits on blur).
        var nameInput = dialog.Locator("input").First;
        await nameInput.FillAsync(matter);
        await nameInput.BlurAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Place hold" }).ClickAsync();

        // It appears in the holds list.
        await Expect(page.Locator(".wb-legalholds").GetByText(matter).First).ToBeVisibleAsync();

        // Select it and release it.
        await page.Locator(".wb-legalholds").GetByText(matter).First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Release hold" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Release", Exact = true }).ClickAsync();

        // After release it's marked released in the list.
        await Expect(page.Locator(".wb-legalholds").GetByText($"{matter} (released)").First).ToBeVisibleAsync();
    }
}
