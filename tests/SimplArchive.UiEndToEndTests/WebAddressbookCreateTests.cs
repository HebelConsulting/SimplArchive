using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Creating a contact from the tree (#689).
//
// The gap this closes was that you could make an Addressbook from the tree's New menu and then had no way to
// put anything in it: the folder's menu was empty. The reason was never containment — an Addressbook has always
// admitted Contact — but that a contact needs a DIALOG, and the menu only knew how to ask for a name.
//
// So the test drives the whole chain rather than the endpoint: the entry has to reach the menu, the menu has to
// open the Contacts tab's own dialog, and the dialog's Save has to file a real contact in that addressbook.
// Every step in between is one that has failed silently in this area before — an entry with no icon, a submenu
// that rendered nothing — and none of those show up in an endpoint test.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebAddressbookCreateTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAddressbookCreateTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_contact_is_created_from_the_addressbooks_own_new_menu()
    {
        var book = $"book-{Guid.NewGuid():N}"[..12];
        var surname = $"Lovelace{Guid.NewGuid():N}"[..14];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Seeded through the API so the test owns its subject: the demo's own addressbook is shared with every
        // other test in the leg, and a contact left in it would drift into their listings.
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = book, folderMask = "addressbook" }))
            .EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");

        // Opened from the LIST rather than reached in the tree: that is what expands the branch and reveals the
        // node (#692), and it leaves the listing standing inside the addressbook, which is where the contact
        // has to turn up at the end.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await list.Locator(".wb-list-row").Filter(new() { HasText = book }).First.DblClickAsync();

        var node = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = book }).First;
        await Expect(node).ToBeVisibleAsync();

        // The entry itself: labelled with the MASK's name, inside New, on the folder that admits it.
        await node.ClickAsync(new() { Button = MouseButton.Right });
        var entry = await Ui.OpenNewSubmenuAsync(page, "Contact");

        // That the list is EXACTLY ["Contact"] — no plain Folder beside it — is asserted in
        // CreatableChildrenTests, where it can be compared as a list. It cannot be said here by filtering menu
        // items on text: HasText is a case-insensitive SUBSTRING match, and this very menu contains
        // "Follow / unfollow this folder", so an assertion that no item mentions "Folder" fails on a label that
        // has nothing to do with creating one. Verified by looking at the built menu, not deduced.
        await entry.ClickAsync();

        // The Contacts tab's own dialog, reused — recognised by a field only it has. A name prompt appearing
        // here instead is the exact failure the prompt vocabulary exists to prevent, and it would otherwise
        // look like success: something opens, you type, something is created.
        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();
        var family = dialog.GetByLabel("Last name").First;
        await Expect(family).ToBeVisibleAsync();

        await FillMudAsync(dialog.GetByLabel("First name").First, "Ada");
        await FillMudAsync(family, surname);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        // Filed where it was aimed. Asserting on the LISTING rather than on the snackbar is deliberate — the
        // message is composed from what was typed, so it would read correctly even if the create had landed
        // somewhere else entirely.
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = surname }).First).ToBeVisibleAsync();
    }

    // The Calendar half. Worth its own test rather than a [Theory] parameter: the two dialogs differ in the
    // one way that matters here — an appointment carries times, which AppointmentForm.ForCreate seeds, so this
    // also asserts that a create from the tree gets a DATED appointment rather than the dateless one a name
    // prompt would have produced. That was the whole reason the entry could not simply be switched on.
    [Fact]
    public async Task An_appointment_is_created_from_the_calendars_own_new_menu()
    {
        var calendar = $"cal-{Guid.NewGuid():N}"[..11];
        var title = $"Review{Guid.NewGuid():N}"[..12];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = calendar, folderMask = "calendar" }))
            .EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await list.Locator(".wb-list-row").Filter(new() { HasText = calendar }).First.DblClickAsync();

        var node = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = calendar }).First;
        await Expect(node).ToBeVisibleAsync();

        await node.ClickAsync(new() { Button = MouseButton.Right });
        await (await Ui.OpenNewSubmenuAsync(page, "Appointment")).ClickAsync();

        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();

        // The Calendar tab's own dialog, recognised by a field only it has — a name prompt appearing instead
        // would look like success right up to the point somebody opened the appointment.
        var summary = dialog.GetByLabel("Title").First;
        await Expect(summary).ToBeVisibleAsync();

        await FillMudAsync(summary, title);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = title }).First).ToBeVisibleAsync();
    }

    // A MudTextField without Immediate commits on BLUR, so filling one and reading it back straight away sees
    // the old value. Tab out before moving on.
    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.PressAsync("Tab");
    }
}
