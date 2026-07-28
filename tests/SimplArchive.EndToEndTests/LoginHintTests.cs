using System.Net;

namespace SimplArchive.EndToEndTests;

// The shared server login page pre-fills the email from the OIDC login_hint carried in the authorize request
// (ADR "Browser-only desktop login + login_hint") — so a returning desktop user, whose logon window passes the
// remembered username as login_hint, doesn't retype it.
[Collection(E2ECollection.Name)]
public class LoginHintTests
{
    private readonly E2EApiFactory _factory;

    public LoginHintTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_page_prefills_the_email_from_login_hint()
    {
        using var client = _factory.CreateClient();

        var returnUrl = "/connect/authorize?client_id=simplarchive-desktop&login_hint=you%40example.com";
        var response = await client.GetAsync($"/Account/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("value=\"you@example.com\"", html);
    }

    [Fact]
    public async Task Login_page_has_no_prefill_without_a_hint()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("value=\"you@example.com\"", html);
    }
}
