using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0534): the Users & groups tab has a "Service accounts" button (gated on
// CanManageServiceAccounts — the demo admin holds every system right) that opens the manager dialog. Creating
// an account shows its client_id + one-time client_secret, then the account appears in the list. Proves the web
// button gating, the create dialog, and the one-time-secret dialog end to end (edit/rotate/revoke are covered
// server-side + by the desktop VM test).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebServiceAccountsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebServiceAccountsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Create_shows_the_one_time_secret_and_lists_the_account()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "web-sa-" + Guid.NewGuid().ToString("N")[..8];

        // Users & groups tab → the Service accounts toolbar button opens the manager dialog.
        await page.Locator(".wb-tab[aria-label=\"Users & groups\"]").First.ClickAsync();
        await page.Locator(".wb-ug-toolbar button[title=\"Service accounts\"]").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.GetByPlaceholder("New service account name")).ToBeVisibleAsync();

        // Type a name (MudTextField commits on blur) and create it.
        await dialog.GetByPlaceholder("New service account name").FillAsync(name);
        await dialog.GetByPlaceholder("New service account name").BlurAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).ClickAsync();

        // The one-time client_id + client_secret dialog appears — dismiss it.
        await Expect(page.GetByText("can't be retrieved again")).ToBeVisibleAsync();
        await page.Locator(".mud-dialog").Last.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

        // The new account is now listed in the manager.
        await Expect(dialog.GetByText(name)).ToBeVisibleAsync();
    }
}
