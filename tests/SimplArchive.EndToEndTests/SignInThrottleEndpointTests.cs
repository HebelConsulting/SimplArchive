using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// Credential guessing, over the wire (ADR 0716, issue #843). The unit tests state the policy; these state
// that the two HTTP doors actually apply it — and, just as important, that the one thing which must never be
// counted is not counted.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class SignInThrottleEndpointTests
{
    private readonly E2EApiFactory _factory;

    public SignInThrottleEndpointTests(E2EApiFactory factory) => _factory = factory;

    private async Task<HttpResponseMessage> TokenAsync(string clientId, string secret)
    {
        using var client = _factory.CreateClient();

        return await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        }));
    }

    [Fact]
    public async Task Guessing_a_client_secret_walls_up_and_the_wall_is_around_that_client_only()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var (otherClientId, otherSecret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        HttpResponseMessage? walled = null;
        for (var attempt = 0; attempt < 12 && walled is null; attempt++)
        {
            var response = await TokenAsync(clientId, $"definitely-not-{secret}-{attempt}");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                walled = response;
            }
            else
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }

        Assert.NotNull(walled);

        // The caller is told how long to wait. Without it a client can only guess, and a client that guesses
        // hammers — which is the traffic this is meant to stop.
        var retryAfter = walled.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.True(retryAfter > TimeSpan.Zero);

        // The correct secret is refused too, for as long as the block lasts. That is the point rather than a
        // side effect: a wall an attacker can walk through by finally guessing right is not a wall.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await TokenAsync(clientId, secret)).StatusCode);

        // …and only that client. Everything else the installation runs keeps working, which is what makes a
        // per-identity block safe to ship on an endpoint every integration shares.
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(otherClientId, otherSecret)).StatusCode);
    }

    [Fact]
    public async Task A_dav_client_probing_without_credentials_is_never_throttled()
    {
        using var anonymous = _factory.CreateClient();

        // Every DAV client on every platform opens with an unauthenticated request and expects the 401 that
        // tells it which scheme to use — Finder, Explorer and every calendar client alike. Counting those as
        // failed attempts would throttle the mount before anyone had typed a password, and it would do it to
        // the honest case only: an attacker sends credentials.
        for (var probe = 0; probe < 30; probe++)
        {
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/SimplArchive");
            var response = await anonymous.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Guessing_a_dav_password_walls_up_every_door_that_password_opens()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"throttle-{Guid.NewGuid():N}@e2e.local";
        const string password = "throttle-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Throttle User");

        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });
        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;

        AuthenticationHeaderValue Basic(string secret) => new(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{secret}")));

        using var dav = _factory.CreateClient();

        async Task<HttpResponseMessage> PropFindAsync(string url, AuthenticationHeaderValue auth)
        {
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url) { Headers = { Authorization = auth } };
            request.Headers.TryAddWithoutValidation("Depth", "0");

            return await dav.SendAsync(request);
        }

        var walled = false;
        for (var attempt = 0; attempt < 12 && !walled; attempt++)
        {
            var response = await PropFindAsync("/SimplArchive", Basic($"wrong-{attempt}"));
            walled = response.StatusCode == HttpStatusCode.TooManyRequests;
            if (!walled)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }

        Assert.True(walled, "guessing the DAV password was never refused");

        // CalDAV is a different gateway, verified by different code — but the SAME password, so it must be
        // the same budget. A throttle an attacker escapes by knocking on the next door is decoration.
        Assert.Equal(
            HttpStatusCode.TooManyRequests, (await PropFindAsync("/caldav", Basic(davPassword))).StatusCode);
    }
}
