using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of issue #424: with the app running in German, a refusal that comes from the SERVER must reach
// the user in German. It did not — the API's Problem Details `detail` is English (its 153 exception classes
// carry their message as a constructor literal, so no Accept-Language handling can reach them) and the client
// displayed it verbatim, so a German user got German until something went wrong and English exactly when it
// mattered. The client now maps the machine `errorCode` to its own localised text (ApiErrorText).
//
// The refusal is real, not mocked: breaking ACL inheritance on a repository ROOT is always rejected
// (CANNOT_CHANGE_ROOT_INHERITANCE, 400 — a root has no parent to inherit from). The desktop suite provokes the
// SAME refusal through SetInheritanceAsync (DesktopApiErrorLocalizationTests), so both clients are held to one
// guarantee. NOTE: that the Manage-access dialog offers the toggle on a root at all is its own defect (#426) — when
// that is fixed, this test needs a different refusal to provoke, not deleting.
//
// Asserts the German sentence itself rather than "not the English one": an inequality assertion also passes when
// the message is empty or has fallen back to the generic sentence, the two most likely ways this breaks.
[Collection(UiCollection.Name)]
public class WebApiErrorLocalizationTests
{
    private readonly SelfHostedAppFixture _app;

    public WebApiErrorLocalizationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_server_refusal_reaches_the_user_in_their_language()
    {
        // Log in first, then switch with the real flag switcher (as WebLocalizationTests does), rather than
        // booting the context in German: the login helper's own locators are English.
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-langbtn").ClickAsync();
        await page.GetByText("Deutsch").First.ClickAsync();

        // The app reloads (silent re-auth) in German — the Repositories tab's label becomes "Archive".
        await Expect(page.Locator(".wb-tab").First).ToHaveAttributeAsync("aria-label", "Archive", new() { Timeout = 25000 });

        // Manage access on the repository ROOT, from the tree context menu.
        var root = page.Locator("[data-pane='tree']").Locator(".mud-treeview-item-content")
            .Filter(new() { HasText = "Demo Repository" }).First;
        await Expect(root).ToBeVisibleAsync();
        await root.ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator(".mud-menu-item").Filter(new() { HasText = "Zugriff verwalten" }).First.ClickAsync();

        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();

        // Break inheritance → confirm. The confirmation is a second .mud-dialog carrying the confirm prompt, and
        // its Yes button repeats the toggle's label, so scope the click to that dialog.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Vererbung aufheben" }).ClickAsync();
        var confirm = page.Locator(".mud-dialog").Filter(new() { HasText = "Die geerbten Berechtigungen werden hierher kopiert" });
        await Expect(confirm).ToBeVisibleAsync();
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Vererbung aufheben" }).ClickAsync();

        // The server refuses, and the user reads it in German.
        var snackbar = page.Locator(".mud-snackbar");
        await Expect(snackbar).ToContainTextAsync(
            "Die Vererbung kann an einem Archiv nicht geändert werden — es gibt keinen übergeordneten Ordner, von dem geerbt werden könnte.");

        // Never the API's English prose, and never the generic fallback — the code is mapped, so the message is
        // about THIS refusal.
        await Expect(snackbar).Not.ToContainTextAsync("Inheritance can't be changed");
        await Expect(snackbar).Not.ToContainTextAsync("Die Aktion wurde vom Server abgelehnt.");
    }
}
