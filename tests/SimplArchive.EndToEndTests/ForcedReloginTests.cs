using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace SimplArchive.EndToEndTests;

// The desktop "log out / switch user" mechanism (ADR "Desktop logout / switch user"): once a browser session
// cookie exists, a normal /connect/authorize silently SSOs the same user — so a second tenant/user could never
// log in. prompt=login must force the login page even with a live cookie. This drives the raw OAuth endpoints
// with a persistent cookie jar to prove both behaviours.
[Collection(E2ECollection.Name)]
public class ForcedReloginTests
{
    private readonly E2EApiFactory _factory;

    public ForcedReloginTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Prompt_login_forces_the_login_page_even_with_a_live_session_cookie()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"relogin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "relog-1234", "Relogin User");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        // Full interactive login → sets the interim session cookie on this client.
        await LogInAsync(client, email, "relog-1234");

        // A normal authorize now silently SSOs (the cookie is live) → 302 straight to the callback with a code,
        // NOT to the login page.
        var sso = (await client.GetAsync(Authorize(prompt: null))).Headers.Location!.ToString();
        Assert.DoesNotContain("/Account/Login", sso);
        Assert.Contains("code=", sso);

        // With prompt=login, the same live cookie is ignored → 302 to the login page (forced re-authentication).
        var forced = (await client.GetAsync(Authorize(prompt: "login"))).Headers.Location!.ToString();
        Assert.Contains("/Account/Login", forced);
    }

    private static string Authorize(string? prompt)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        var parts = new List<string>
        {
            "client_id=blazor-client", "response_type=code",
            $"redirect_uri={Uri.EscapeDataString("http://localhost/authentication/login-callback")}",
            "scope=openid", $"code_challenge={challenge}", "code_challenge_method=S256", "state=x",
        };
        if (prompt is not null) parts.Add($"prompt={prompt}");
        return "/connect/authorize?" + string.Join('&', parts);
    }

    private static async Task LogInAsync(HttpClient client, string email, string password)
    {
        var loginPath = (await client.GetAsync(Authorize(prompt: null))).Headers.Location!.ToString();
        var loginHtml = await client.GetStringAsync(loginPath);
        var antiforgery = Regex.Match(loginHtml, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://localhost" + loginPath).Query)["ReturnUrl"].ToString();
        var login = await client.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = antiforgery,
        }));
        // Follow through to the callback so the session cookie is fully established.
        var next = login.Headers.Location!.ToString();
        for (var i = 0; i < 8; i++)
        {
            var r = await client.GetAsync(next);
            if (r.Headers.Location is not { } loc) break;
            var abs = loc.IsAbsoluteUri ? loc : new Uri(new Uri("http://localhost"), loc);
            if (QueryHelpers.ParseQuery(abs.Query).ContainsKey("code")) break;
            next = abs.ToString();
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
