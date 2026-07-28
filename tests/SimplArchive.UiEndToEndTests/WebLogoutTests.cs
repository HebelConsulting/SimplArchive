using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow: logging out returns the app to the "please log in" prompt and clears the logged-in identity —
// completes the auth lifecycle (login is covered by the other tests).
[Collection(UiCollection.Name)]
public class WebLogoutTests
{
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
        // the page" error the plain-link navigation used to produce).
        await Expect(page.GetByText("You are logged out")).ToBeVisibleAsync();
    }
}
