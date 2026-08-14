using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A token the SERVER rejects sends the user to sign in (issue #509).
//
// This is the case no existing catch covered: AccessTokenNotAvailableException fires when the CLIENT has no
// token, whereas here it has one and the server repudiates it — the request goes out happily, comes back 401,
// and every tab reported its own unrelated failure ("Could not load recycle bin") while the stale token sat in
// storage looking valid.
//
// So the test corrupts the stored access token rather than clearing it. Clearing it would exercise the path
// that already worked and prove nothing about this one.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSessionExpiredTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSessionExpiredTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_token_the_server_rejects_redirects_to_sign_in()
    {
        var page = await Ui.LoginAsync(_app);

        // Make the server's answer a 401, which is the condition under test. Tampering with the stored token
        // was tried first and proved nothing: AuthorizationMessageHandler takes the token from the in-memory
        // session rather than re-reading sessionStorage, so the request went out with a VALID token and
        // succeeded. Intercepting the response tests the handler instead of the token plumbing.
        var intercepted = 0;
        await page.RouteAsync("**/api/recycle-bin*", async route =>
        {
            intercepted++;
            await route.FulfillAsync(new()
            {
                Status = 401,
                ContentType = "application/problem+json",
                Body = """{"title":"Unauthorized","status":401}""",
            });
        });

        // Any tab whose load calls the API will do; the Recycle bin is the one that reported the wrong thing.
        await page.Locator(".wb-tab[aria-label=\"Recycle bin\"]").First.ClickAsync();

        // The redirect is the fix. Without it the user stays put and gets a snackbar blaming the recycle bin.
        await Expect(page).ToHaveURLAsync(new Regex("authentication/login"), new() { Timeout = 15000 });

        // Anti-vacuous: if the route never matched, the assertion above would be proving that some unrelated
        // navigation happens to land on the login page.
        Assert.True(intercepted > 0, "the 401 was never served, so this test proved nothing");
    }
}
