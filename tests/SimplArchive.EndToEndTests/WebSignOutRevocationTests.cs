using System.Net;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// Signing out of the web client revokes that client's sessions on the server, not just the cookie (#1578).
/// </summary>
/// <remarks>
/// <c>/Account/Logout</c> is a plain browser GET with no bearer token, so it revokes by WHO is signing out — the
/// cookie names them — and WHICH client: every <c>blazor-client</c> session of that user, and nothing issued to
/// the desktop (owner, 2026-10-05: "that client's sessions").
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class WebSignOutRevocationTests(E2EApiFactory factory)
{
    [Fact]
    public async Task Signing_out_of_the_browser_kills_its_access_token_on_the_next_request()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"signout-{Guid.NewGuid():N}@e2e.local";
        await factory.SeedUserAsync(tenantId, email, "correct-horse-1234", "Signs Out");

        var (browser, token) = await factory.SignInBrowserAsync(email, "correct-horse-1234");
        using (browser)
        {
            Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(token));

            using var signedOut = await browser.GetAsync("/Account/Logout");
            Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        }

        // Before #1578 this stayed 200 for up to 15 minutes: the token outlived the sign-out that ended its session.
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(token));
    }

    [Fact]
    public async Task Signing_out_of_one_browser_ends_every_browser_session_of_that_user()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"twobrowsers-{Guid.NewGuid():N}@e2e.local";
        await factory.SeedUserAsync(tenantId, email, "correct-horse-1234", "Two Browsers");

        var (first, firstToken) = await factory.SignInBrowserAsync(email, "correct-horse-1234");
        var (second, secondToken) = await factory.SignInBrowserAsync(email, "correct-horse-1234");
        using (first)
        using (second)
        {
            using var _ = await first.GetAsync("/Account/Logout");
        }

        // "That client's sessions", plural: the browser sign-out has no token to say which session it is ending,
        // so it ends the web client's sessions of this user — a shared machine's forgotten second tab included.
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(firstToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(secondToken));
    }

    private async Task<HttpStatusCode> WhoAmIAsync(string token)
    {
        using var client = factory.CreateAuthedClient(token);
        using var response = await client.GetAsync("/api/diagnostics/whoami");
        return response.StatusCode;
    }
}
