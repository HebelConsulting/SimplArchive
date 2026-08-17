using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web Contacts and Calendar tabs (#564, ADR 0624) — the twins of the desktop tabs, which are the
// reference (ADR 0511). A [Theory] over both, because the pair is ONE surface: what is asserted of one is
// asserted of the other, and a divergence should fail rather than be discovered later in a screenshot.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebContactsCalendarTests
{
    private readonly SelfHostedAppFixture _app;

    public WebContactsCalendarTests(SelfHostedAppFixture app) => _app = app;

    [Theory]
    [InlineData("Contacts", "My Contacts", "New contact")]
    [InlineData("Calendar", "My Calendar", "New appointment")]
    public async Task The_tab_opens_on_the_personal_collection_with_it_ticked(string tab, string collection, string newLabel)
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator($".wb-tab[aria-label='{tab}']").First.ClickAsync();

        // The personal collection is listed, parent-qualified, and TICKED — a tab that needs a click before it
        // shows anything reads as broken.
        var row = page.Locator($"text=/Personal / {collection}/").First;
        await Expect(row).ToBeVisibleAsync();

        var check = page.Locator($".mud-checkbox input[aria-label*='{collection}']").First;
        await Expect(check).ToBeCheckedAsync();

        // Writable, so New is offered rather than sitting inert.
        await Expect(page.Locator($"button[aria-label='{newLabel}']").First).ToBeEnabledAsync();
    }

    [Theory]
    [InlineData("Contacts", "My Contacts")]
    [InlineData("Calendar", "My Calendar")]
    public async Task Unticking_the_last_collection_disables_creating(string tab, string collection)
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator($".wb-tab[aria-label='{tab}']").First.ClickAsync();

        var check = page.Locator($".mud-checkbox input[aria-label*='{collection}']").First;
        await Expect(check).ToBeCheckedAsync();
        await check.ClickAsync();
        await Expect(check).Not.ToBeCheckedAsync();

        // The affordance must not claim it can write somewhere the user has not chosen (ADR 0543's family:
        // an action that is not available should not be offered).
        var newButton = page.Locator("button[aria-label='New contact'], button[aria-label='New appointment']").First;
        await Expect(newButton).ToBeDisabledAsync();
    }

    // The two tabs must not show each other's collections: they ask the same endpoint with a different kind,
    // and a kind that stopped being applied would look like "everything works" until someone counted the rows.
    [Fact]
    public async Task Neither_tab_lists_the_other_kind()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label='Contacts']").First.ClickAsync();
        await Expect(page.Locator("text=/Personal / My Contacts/").First).ToBeVisibleAsync();
        await Expect(page.Locator("text=/Personal / My Calendar/")).ToHaveCountAsync(0);

        await page.Locator(".wb-tab[aria-label='Calendar']").First.ClickAsync();
        await Expect(page.Locator("text=/Personal / My Calendar/").First).ToBeVisibleAsync();
        await Expect(page.Locator("text=/Personal / My Contacts/")).ToHaveCountAsync(0);
    }
}
