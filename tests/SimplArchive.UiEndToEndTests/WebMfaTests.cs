using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "MFA (interactive login, TOTP)"): the corner account menu offers to enable two-factor auth,
// and the setup dialog renders the enrollment QR + code entry. The dialog is cancelled without confirming, so
// the shared demo admin is never actually given MFA (enrollment only stages a pending secret; login is
// unaffected until a code confirms it) — the full enable/login loop is covered by the container E2E MfaTests.
[Collection(UiCollection.Name)]
public class WebMfaTests
{
    private readonly SelfHostedAppFixture _app;

    public WebMfaTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Account_menu_opens_the_two_factor_setup_dialog()
    {
        var page = await Ui.LoginAsync(_app);

        // Open the corner account menu and start two-factor setup.
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Enable two-factor auth").First.ClickAsync();

        // The setup dialog renders the enrollment QR and the code entry + Enable button. The QR image is a data
        // URL produced by an async POST /api/users/me/mfa/enroll round-trip, so give it a network-appropriate
        // timeout rather than the 5s default (which flaked under CI load).
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator("img[alt='Authenticator QR code']")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Enable", Exact = true })).ToBeVisibleAsync();

        // Cancel — don't confirm, so the demo admin never actually gets MFA enabled.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }
}
