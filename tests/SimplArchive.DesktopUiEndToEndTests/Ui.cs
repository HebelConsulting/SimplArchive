using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimplArchive.UiEndToEndTests;

// Desktop-side helper: obtains a demo-admin User access token by driving the real OAuth2 Authorization Code +
// PKCE flow over HTTP (the same flow the SPA performs) — used to point the DesktopClient's SimplArchiveApiClient
// at the self-hosted API without a loopback-browser login. (The web UI project's Ui.cs additionally has the
// Playwright LoginAsync; this desktop copy is HTTP-only, no browser.)
internal static class Ui
{
    public static async Task<string> GetUserTokenAsync(string baseUrl, string? email = null, string? password = null)
    {
        email ??= SelfHostedAppFixture.AdminEmail;
        password ??= SelfHostedAppFixture.AdminPassword;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseCookies = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        const string redirectPath = "/authentication/login-callback";
        var authorize = "/connect/authorize?" + string.Join('&', new[]
        {
            "client_id=blazor-client", "response_type=code", $"redirect_uri={Uri.EscapeDataString(baseUrl + redirectPath)}",
            "scope=openid", $"code_challenge={challenge}", "code_challenge_method=S256", "state=x",
        });

        var loginPath = (await http.GetAsync(authorize)).Headers.Location!.ToString();
        var loginHtml = await http.GetStringAsync(loginPath);
        var antiforgery = Regex.Match(loginHtml, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var returnUrl = Regex.Match(loginPath, @"ReturnUrl=([^&]+)").Groups[1].Value;

        var login = await http.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = Uri.UnescapeDataString(returnUrl),
            ["__RequestVerificationToken"] = antiforgery,
        }));

        var next = login.Headers.Location!.ToString();
        string? code = null;
        for (var i = 0; i < 8 && code is null; i++)
        {
            var response = await http.GetAsync(next);
            if (response.Headers.Location is not { } location)
            {
                break;
            }

            var abs = location.IsAbsoluteUri ? location : new Uri(new Uri(baseUrl), location);
            var m = Regex.Match(abs.Query, @"[?&]code=([^&]+)");
            code = m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
            next = abs.ToString();
        }

        var tokenResponse = await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = baseUrl + redirectPath,
            ["client_id"] = "blazor-client",
            ["code_verifier"] = verifier,
        }));
        tokenResponse.EnsureSuccessStatusCode();
        var json = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
