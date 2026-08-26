using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow: logging out returns the app to the "please log in" prompt and clears the logged-in identity —
// completes the auth lifecycle (login is covered by the other tests).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public partial class WebLogoutTests
{
    [System.Text.RegularExpressions.GeneratedRegex("^log ?in$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LoginTextRegex();

    private readonly SelfHostedAppFixture _app;

    public WebLogoutTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Logging_out_returns_to_the_login_prompt()
    {
        var page = await Ui.LoginAsync(_app);
        await Expect(page.Locator(".wb-appbar").GetByText(SelfHostedAppFixture.AdminDisplayName)).ToBeVisibleAsync();

        // Log out lives in the corner account menu now.
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Log out").ClickAsync();

        // Logout completes (RemoteAuthenticatorView's logout succeeds instead of the "not initiated from within
        // the page" error the plain-link navigation used to produce). Still the landing page now that logout
        // also ends the server session: the sign-out endpoint returns here rather than past it.
        await Expect(page.GetByText("You are logged out")).ToBeVisibleAsync();
    }

    // Logging out clears THIS CLIENT'S tokens; it does not end the session on the server, because there is no
    // end-session endpoint (ADR 0334). So the next /connect/authorize found the OpenIddict cookie still valid
    // and signed the SAME user straight back in — no prompt, no way to switch account, and a screen that said
    // "please log in" while doing the opposite. Reported from real use: "stupid if you want to test with
    // another user."
    //
    // The fix is prompt=login on the explicit login button, which is how the desktop has always done it. This
    // test is about being ASKED — it asserts the credential form appears, not that any particular user gets in.
    [Fact]
    public async Task Logging_in_after_a_logout_asks_who_you_are()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Log out").ClickAsync();
        await Expect(page.GetByText("You are logged out")).ToBeVisibleAsync();

        // A fresh navigation, which is the move that used to restore the session: the SPA's silent sign-in ran
        // against a cookie logout had never touched, and the workbench came straight back.
        await page.GotoAsync(_app.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByText("SimplArchive").First.WaitForAsync();
        // Matched the way Ui.LoginAsync matches it — by TEXT with the same anchored regex. A role+Name locator
        // found nothing here, and an anchored text match is also what keeps this honest if the label is
        // localised: it is the same string the helper already relies on.
        await page.GetByText(LoginTextRegex()).First.ClickAsync();

        // The credential form — the whole point. Without prompt=login this lands straight back in the
        // workbench, which is a PASS-shaped failure: the app works, it just refuses to let you be anyone else.
        await page.WaitForSelectorAsync("input[name='Email'], input[type='email']");

        // And the counterpart, so this cannot pass by the app merely being broken: the workbench is NOT shown.
        await Expect(page.Locator(".wb-appbar").GetByText(SelfHostedAppFixture.AdminDisplayName)).ToBeHiddenAsync();
    }
}
