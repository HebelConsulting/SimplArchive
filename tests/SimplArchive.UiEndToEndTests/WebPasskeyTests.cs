using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A browser UI flow with a CDP virtual authenticator (ADR "WebAuthn passkeys as a second factor"): the demo
// admin registers a passkey (real navigator.credentials.create → Fido2 attestation → stored), then signs out and
// signs back in USING the passkey at the login challenge (navigator.credentials.get → Fido2 assertion). Cleans
// up the passkey from the shared demo admin afterwards so other tests' logins are unaffected.
[Collection(UiCollection.Name)]
public class WebPasskeyTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPasskeyTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Register_a_passkey_then_sign_in_with_it()
    {
        var page = await Ui.LoginAsync(_app);
        try
        {
            // A virtual authenticator on this context — auto-completes the WebAuthn ceremonies.
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

            // Register a passkey via the account menu → Passkeys dialog.
            await page.Locator(".wb-userbox").ClickAsync();
            await page.GetByText("Passkeys").First.ClickAsync();
            var dialog = page.Locator(".mud-dialog");
            await Expect(dialog).ToBeVisibleAsync();
            var nameInput = dialog.Locator("input").First;
            await nameInput.FillAsync("E2E Virtual Key");
            await nameInput.PressAsync("Tab"); // MudTextField commits its value on blur
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Add passkey" }).ClickAsync();

            // The passkey now appears in the list.
            await Expect(dialog.GetByText("E2E Virtual Key")).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();

            // Sign out (lands on the RemoteAuthenticatorView "You are logged out" page).
            await page.Locator(".wb-userbox").ClickAsync();
            await page.GetByText("Log out").First.ClickAsync();
            await Expect(page.GetByText("You are logged out")).ToBeVisibleAsync();

            // Client-side logout leaves both the server OpenIddict cookie alive (ADR 0211 has no end-session
            // endpoint) and the SPA's cached OIDC token in web storage — either would let the app silently
            // re-authenticate and skip the login page. Clear both to force a fresh interactive login that
            // actually reaches the second-factor challenge.
            await page.Context.ClearCookiesAsync();
            await page.EvaluateAsync("() => { window.sessionStorage.clear(); window.localStorage.clear(); }");

            await page.GotoAsync(_app.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByText("SimplArchive").First.WaitForAsync();
            await page.GetByText(new System.Text.RegularExpressions.Regex("^log ?in$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).First.ClickAsync();
            await page.WaitForSelectorAsync("input[name='Email'], input[type='email']");
            await page.FillAsync("input[name='Email'], input[type='email']", SelfHostedAppFixture.AdminEmail);
            await page.FillAsync("input[name='Password'], input[type='password']", SelfHostedAppFixture.AdminPassword);
            await page.ClickAsync("button[type='submit'], input[type='submit']");

            // The challenge step offers the passkey — use it (the virtual authenticator asserts automatically).
            await page.GetByRole(AriaRole.Button, new() { Name = "Use a passkey" }).ClickAsync();

            // Signed in with the passkey — the workbench shows the admin's display name.
            await page.Locator(".wb-appbar").GetByText(SelfHostedAppFixture.AdminDisplayName).WaitForAsync();
        }
        finally
        {
            // Remove the passkey from the shared demo admin so other tests' password logins go straight in.
            await using var conn = new NpgsqlConnection(_app.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM \"WebAuthnCredentials\";", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
