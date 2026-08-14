using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The clear (×) on a search field: it empties the field, Esc does the same from the keyboard, and the caret
// stays put (#503).
//
// Honest note on the focus assertions: they were MEASURED to be non-discriminating here. Commenting the
// component's FocusAsync out leaves them passing, because MudBlazor's clear button is an adornment inside the
// input and never takes focus away from it — so on the web the caret was never lost. (On the desktop it
// genuinely was; the `--searchclear-test` hook fails there without the fix.) They stay because they pin the
// behaviour this feature REQUIRES, whoever happens to provide it — if a MudBlazor upgrade makes that button
// focusable, this is what says so. They are not evidence that our own code works.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSearchFieldClearTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSearchFieldClearTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Clearing_the_search_field_empties_it_and_leaves_the_caret_in_it()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        await input.FillAsync("Invoice");

        // The clear button is MudBlazor's adornment inside the field, so it is found from the field's own wrapper
        // rather than the page — the Search tab has other buttons that would match a looser locator.
        var field = page.Locator(".wb-search-bar .mud-input-control").First;
        await field.Locator("button").Last.ClickAsync();

        await Expect(input).ToHaveValueAsync(string.Empty);
        await Expect(input).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Escape_clears_the_field_when_it_has_text_and_is_ignored_when_it_does_not()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        await input.FillAsync("Invoice");
        await input.PressAsync("Escape");

        await Expect(input).ToHaveValueAsync(string.Empty);
        await Expect(input).ToBeFocusedAsync();

        // A second Escape has nothing to clear. It must be a no-op HERE rather than doing something else, or the
        // gesture would stop meaning one thing across the app (ADR 0550) — the field keeps focus and stays empty.
        await input.PressAsync("Escape");
        await Expect(input).ToHaveValueAsync(string.Empty);
        await Expect(input).ToBeFocusedAsync();
    }
}
