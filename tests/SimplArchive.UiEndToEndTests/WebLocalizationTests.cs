using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web i18n slice (ADR "Web UI localization — shared resources" + "Web language flag switcher"): the workbench
// chrome is localized from the shared resources, and the flag-dropdown language switcher (to the right of the
// notifications bell) persists the choice + reloads the app in that language. Verifies the German satellite
// actually loads in Blazor WASM (Repositories tab → "Archive").
[Collection(UiCollection.Name)]
public class WebLocalizationTests
{
    private readonly SelfHostedAppFixture _app;

    public WebLocalizationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Switching_the_language_localizes_the_workbench()
    {
        var page = await Ui.LoginAsync(_app);

        // Default English: the first tab reads "Repositories".
        await Expect(page.Locator(".wb-tab").First).ToContainTextAsync("Repositories");

        // Language flag dropdown (right of the notifications bell) → the "Deutsch" item.
        await page.Locator(".wb-langbtn").ClickAsync();
        await page.GetByText("Deutsch").First.ClickAsync();

        // The app reloads (silent re-auth) in German — the Repositories tab now reads "Archive".
        await Expect(page.Locator(".wb-tab").First).ToContainTextAsync("Archive", new() { Timeout = 25000 });

        // The app-bar account menu is localized too (extending ADR "Web UI localization — shared resources"):
        // opening it shows the German "Log out" = "Abmelden".
        await page.Locator(".wb-userbox").ClickAsync();
        await Expect(page.GetByText("Abmelden").First).ToBeVisibleAsync();
    }
}
