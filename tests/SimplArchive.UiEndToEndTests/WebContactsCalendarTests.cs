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
    [InlineData("Contacts", "My Addressbook", "New contact")]
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
    [InlineData("Contacts", "My Addressbook")]
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

    // New contact (#631) through the browser: the ribbon button opens the structured editor EMPTY, Save creates
    // the document, and the row appears in the list.
    //
    // The web client had no editor at all before this — its detail pane was read-only text — so this asserts
    // the whole half that was missing, not just the button.
    [Fact]
    public async Task New_contact_opens_the_editor_and_creates_the_contact()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label='Contacts']").First.ClickAsync();

        await page.Locator("button[aria-label='New contact']").First.ClickAsync();

        // The repeatable rows are what makes this the EDITOR rather than a create-shaped subset: a blank form
        // opens with one e-mail and one phone row, so a create form whose only visible fields are names does
        // not read as one that cannot hold them.
        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator("text=Email").First).ToBeVisibleAsync();

        var surname = $"Lovelace{Guid.NewGuid().ToString("N")[..6]}";
        await FillMudAsync(dialog.Locator("input").Nth(0), "Ada");
        await FillMudAsync(dialog.Locator("input").Nth(1), surname);

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        // Listed under the name the SERVER filed it as — the create is what decides it, and a client that
        // asserted its own composition would pass while the two disagreed.
        await Expect(page.Locator($"text=Ada {surname}").First).ToBeVisibleAsync();
    }

    // …and Edit reopens the same dialog on the stored card. Run after a create so there is something to edit,
    // and because the pair is the point: one dialog serving both paths is what keeps the create from being a
    // funnel that drops fields the editor models.
    [Fact]
    public async Task Edit_reopens_the_same_editor_on_the_stored_card()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label='Contacts']").First.ClickAsync();

        await page.Locator("button[aria-label='New contact']").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();

        var org = $"Engines{Guid.NewGuid().ToString("N")[..6]}";
        await FillMudAsync(dialog.Locator("input").Nth(0), "Grace");
        await FillMudAsync(dialog.Locator("input").Nth(1), "Hopper");
        await FillMudAsync(dialog.Locator("input").Nth(2), org);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        // Edit is addressed from the ROW the user clicked (ADR 0559), so select it first.
        await page.Locator("text=Grace Hopper").First.ClickAsync();
        await page.Locator("button[aria-label='Edit contact']").First.ClickAsync();

        // The organisation survived the create and is what the editor opens on — which proves the create wrote
        // a real vCard and the reader parses it, rather than the row merely bearing the right name.
        // Read as the input's VALUE, not as an attribute selector: MudBlazor sets it as a DOM property, so
        // `input[value='…']` matches nothing however correct the field is.
        var editor = page.Locator(".mud-dialog").First;
        await Expect(editor).ToBeVisibleAsync();
        await Expect(editor.Locator("input").Nth(2)).ToHaveValueAsync(org);
    }

    // The two tabs must not show each other's collections: they ask the same endpoint with a different kind,
    // and a kind that stopped being applied would look like "everything works" until someone counted the rows.
    [Fact]
    public async Task Neither_tab_lists_the_other_kind()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label='Contacts']").First.ClickAsync();
        await Expect(page.Locator("text=/Personal / My Addressbook/").First).ToBeVisibleAsync();
        await Expect(page.Locator("text=/Personal / My Calendar/")).ToHaveCountAsync(0);

        await page.Locator(".wb-tab[aria-label='Calendar']").First.ClickAsync();
        await Expect(page.Locator("text=/Personal / My Calendar/").First).ToBeVisibleAsync();
        await Expect(page.Locator("text=/Personal / My Addressbook/")).ToHaveCountAsync(0);
    }

    // MudTextField (no Immediate) commits its bound value on the change event, i.e. on blur — fill then blur so
    // the value reaches the model before Save reads it.
    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.EvaluateAsync("el => el.blur()");
    }
}
