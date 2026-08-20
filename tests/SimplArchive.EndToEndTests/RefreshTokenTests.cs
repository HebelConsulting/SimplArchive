using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.EndToEndTests;

// Renewing a session without the user present. The desktop client held ONE access token from login and sent it
// for ever, so an hour later every request came back 401 with nothing renewing anything — reported as "I thought
// the client rotates the tokens even in the background", which it did not, because the server issued nothing to
// rotate with: no refresh flow, no offline_access scope, no grant on the desktop client's registration.
//
// Driven through the REAL authorization-code flow against the real OpenIddict server rather than through a
// stubbed token endpoint, because every one of those three was missing independently and a stub would have
// proved only that the client can parse a response we wrote ourselves.
[Collection(E2ECollection.Name)]
public class RefreshTokenTests
{
    private readonly E2EApiFactory _factory;

    public RefreshTokenTests(E2EApiFactory factory) => _factory = factory;

    private const string DesktopClientId = "simplarchive-desktop";
    private const string DesktopRedirect = "http://127.0.0.1:8765/callback";

    [Fact]
    public async Task A_desktop_login_yields_a_refresh_token_that_renews_the_session()
    {
        var (email, password, _) = await SeedUserAsync();

        var tokens = await SignInAsync(email, password);

        // offline_access asked for it, and the client registration is what permits it. Before this change all
        // three were absent, so this property simply did not exist on the response.
        Assert.True(tokens.TryGetProperty("refresh_token", out var refresh), "the login issued no refresh token.");
        var refreshToken = refresh.GetString()!;
        var firstAccess = tokens.GetProperty("access_token").GetString()!;

        var renewed = await RenewAsync(refreshToken);
        var secondAccess = renewed.GetProperty("access_token").GetString()!;

        // A genuinely new token, not the same one handed back.
        Assert.NotEqual(firstAccess, secondAccess);

        // And it WORKS — the point of the exercise. Asserted by using it, not by inspecting its claims.
        using var api = _factory.CreateAuthedClient(secondAccess);
        var whoami = await api.GetAsync("/api/diagnostics/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
    }

    [Fact]
    public async Task The_refresh_token_rotates_and_the_spent_one_is_refused()
    {
        var (email, password, _) = await SeedUserAsync();
        var tokens = await SignInAsync(email, password);
        var first = tokens.GetProperty("refresh_token").GetString()!;

        var renewed = await RenewAsync(first);
        var second = renewed.GetProperty("refresh_token").GetString()!;

        // Rotation: a new refresh token comes back, and it is not the one just spent. This is what makes a
        // leaked token good for a single use rather than for its whole lifetime — and it is why the client must
        // store the rotated one, since keeping the old would fail its NEXT renewal.
        Assert.NotEqual(first, second);

        using var client = _factory.CreateClient();

        // INSIDE the reuse leeway the spent token still works, and that is deliberate rather than a hole: a
        // client whose refresh response was lost to a network blip retries with the token it still holds, and
        // without the grace that ordinary event would sign the user out. Asserted rather than glossed over,
        // because the first version of this test expected an immediate refusal and was simply wrong about
        // what the server does.
        var withinLeeway = await PostRefreshAsync(client, first);
        Assert.Equal(HttpStatusCode.OK, withinLeeway.StatusCode);

        // PAST it, the redeemed token is refused — the property that actually matters. The window is one
        // second in the test host (30 in production), which is the only reason this is cheap enough to assert.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var afterLeeway = await PostRefreshAsync(client, first);
        Assert.Equal(HttpStatusCode.BadRequest, afterLeeway.StatusCode);
    }

    // A refresh token outlives the access token by design, so without a re-check a user deactivated ten minutes
    // ago would go on minting fresh access tokens until it expired. The token pipeline gets exactly one chance
    // to ask, and this is it.
    [Fact]
    public async Task A_deactivated_user_cannot_renew()
    {
        var (email, password, userId) = await SeedUserAsync();
        var tokens = await SignInAsync(email, password);
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // Deactivated directly — the same scope-off-the-factory pattern the other E2E tests use to arrange
        // state the API has no endpoint for (see TokenEndpointRefusalTests).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        var refused = await PostRefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<(string Email, string Password, Guid UserId)> SeedUserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"refresh-{Guid.NewGuid():N}@e2e.local";
        const string password = "refresh-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Refresh User");
        return (email, password, userId);
    }

    private async Task<JsonElement> RenewAsync(string refreshToken)
    {
        using var client = _factory.CreateClient();
        var response = await PostRefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = DesktopClientId,
        }));

    /// <summary>The desktop client's own authorization-code + PKCE flow, asking for offline_access.</summary>
    private async Task<JsonElement> SignInAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

        var authorize = "/connect/authorize?" + string.Join('&', new[]
        {
            $"client_id={DesktopClientId}", "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(DesktopRedirect)}",
            $"scope={Uri.EscapeDataString("openid offline_access")}",
            $"code_challenge={challenge}", "code_challenge_method=S256", "state=x",
        });

        var loginPath = (await client.GetAsync(authorize)).Headers.Location!.ToString();
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

        var next = login.Headers.Location!.ToString();
        string? code = null;
        for (var i = 0; i < 8 && code is null; i++)
        {
            var response = await client.GetAsync(next);
            if (response.Headers.Location is not { } location)
            {
                break;
            }

            var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri("http://localhost"), location);
            code = QueryHelpers.ParseQuery(absolute.Query).TryGetValue("code", out var c) ? c.ToString() : null;
            next = absolute.ToString();
        }

        Assert.False(string.IsNullOrEmpty(code), "the authorization flow returned no code.");

        var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = DesktopRedirect,
            ["client_id"] = DesktopClientId,
            ["code_verifier"] = verifier,
        }));

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
