using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A browser UI flow with a CDP virtual authenticator (ADR "Passwordless passkey login"): with the tenant's
// AllowPasskeyLogin policy on, the demo admin registers a passkey, signs out, and signs back in PASSWORDLESS —
// clicking "Sign in with a passkey" on the login page with no email/password (a discoverable/usernameless
// assertion; the passkey's user handle identifies the user, and it satisfies require-MFA). Cleans up the passkey
// + the tenant flag afterwards so the shared demo tenant/admin is unaffected.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebPasswordlessPasskeyTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPasswordlessPasskeyTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Sign_in_passwordless_with_a_passkey()
    {
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
                    ["hasResidentKey"] = true,     // discoverable/usernameless login needs a resident key
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
            await nameInput.FillAsync("E2E Passwordless Key");
            await nameInput.PressAsync("Tab");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Add passkey" }).ClickAsync();
            await Expect(dialog.GetByText("E2E Passwordless Key")).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();

            // Enable the per-tenant passwordless-login policy (directly — the toggle round-trip is covered by the
            // tenant-settings tests; here we exercise the login).
            await SetAllowPasskeyLoginAsync(true);

            // Sign out + clear the server cookie and cached OIDC token so a fresh interactive login is forced.
            await page.Locator(".wb-userbox").ClickAsync();
            await page.GetByText("Log out").First.ClickAsync();
            await Expect(page.GetByText("You are logged out")).ToBeVisibleAsync();
            await page.Context.ClearCookiesAsync();
            await page.EvaluateAsync("() => { window.sessionStorage.clear(); window.localStorage.clear(); }");

            await page.GotoAsync(_app.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByText("SimplArchive").First.WaitForAsync();
            await page.GetByText(new System.Text.RegularExpressions.Regex("^log ?in$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).First.ClickAsync();
            await page.WaitForSelectorAsync("input[name='Email'], input[type='email']");

            // No email/password — click "Sign in with a passkey"; the virtual authenticator asserts a discoverable
            // credential automatically, its user handle identifies the admin, and the tenant allows it.
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in with a passkey" }).ClickAsync();

            // Signed in passwordless — the workbench shows the admin's display name.
            await page.Locator(".wb-appbar").GetByText(SelfHostedAppFixture.AdminDisplayName).WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        }
        finally
        {
            await using var conn = new NpgsqlConnection(_app.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM \"WebAuthnCredentials\"; UPDATE \"Tenants\" SET \"AllowPasskeyLogin\" = false;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task SetAllowPasskeyLoginAsync(bool value)
    {
        await using var conn = new NpgsqlConnection(_app.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE \"Tenants\" SET \"AllowPasskeyLogin\" = @v;", conn);
        cmd.Parameters.AddWithValue("v", value);
        await cmd.ExecuteNonQueryAsync();
    }
}
