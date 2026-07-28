using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The server-rendered passkey-management page (ADR "Desktop passkey management") the desktop client opens in
// the system browser: with a CDP virtual authenticator, navigate straight to /Account/Passkeys (as the desktop
// does, minus the loopback), register a passkey via the real navigator.credentials.create → Fido2 attestation
// ceremony, confirm it appears in the list, then remove it. Cleans up the shared demo admin's passkeys.
[Collection(UiCollection.Name)]
public class WebPasskeyManagementPageTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPasskeyManagementPageTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Register_and_remove_a_passkey_on_the_page()
    {
        // LoginAsync establishes the auth-server cookie session the page authenticates against.
        var page = await Ui.LoginAsync(_app);
        try
        {
            var cdp = await page.Context.NewCDPSessionAsync(page);
            await cdp.SendAsync("WebAuthn.enable");
            await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
            {
                ["options"] = new Dictionary<string, object>
                {
                    ["protocol"] = "ctap2",
                    ["transport"] = "internal",
                    ["hasResidentKey"] = true,
                    ["hasUserVerification"] = true,
                    ["isUserVerified"] = true,
                    ["automaticPresenceSimulation"] = true,
                },
            });

            // The desktop opens exactly this page in the system browser (here without the loopback param, so it
            // behaves as a standalone management page and re-renders in place after each action).
            await page.GotoAsync($"{_app.BaseUrl}/Account/Passkeys", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Passkeys" })).ToBeVisibleAsync();

            await page.FillAsync("input[name='NewName']", "E2E Desktop Key");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add a passkey" }).ClickAsync();

            // The ceremony runs, the form posts, and the page re-renders with the passkey listed.
            await Expect(page.Locator(".keys").GetByText("E2E Desktop Key")).ToBeVisibleAsync();

            // Remove it — the page re-renders with no passkeys.
            await page.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
            await Expect(page.GetByText("No passkeys yet.")).ToBeVisibleAsync();
        }
        finally
        {
            await using var conn = new NpgsqlConnection(_app.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM \"WebAuthnCredentials\";", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
