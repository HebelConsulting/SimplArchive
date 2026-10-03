using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The issued token's lifetime is the one ADR 0083 chose (#847, A07).
//
// WHY THIS IS WORTH A TEST RATHER THAN A LINE OF CONFIG. ADR 0083 specified 15 minutes and 30 days; nothing
// configured either, so OpenIddict's own defaults (~1 h / 14 d) applied for the life of the project and the
// drift was invisible — a decision recorded in prose and never checked is indistinguishable from one nobody
// took. Reading it off a REAL token is the only assertion that cannot drift back silently: a future options
// change, a different builder, an OpenIddict default that moves, all show up here.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TokenLifetimeTests(E2EApiFactory factory)
{
    [Fact]
    public async Task An_access_token_expires_in_about_fifteen_minutes()
    {
        var (clientId, secret, _) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
            }));

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        // expires_in is what the CLIENT is told, which is the number that actually governs when it refreshes —
        // asserted rather than the server's configured value, because those are two different things and only
        // one of them reaches anybody.
        var expiresIn = payload.GetProperty("expires_in").GetInt32();

        // A window, not an equality: the value is computed from "now" at issue time, so a slow runner can
        // shave a second or two. Wide enough to survive that, narrow enough that the old ~1 h default — the
        // thing this exists to catch — could never pass.
        Assert.InRange(expiresIn, 14 * 60, 15 * 60);
    }

    [Fact]
    public async Task The_window_an_unrevocable_token_stays_valid_is_minutes_not_hours()
    {
        // The same fact said as the SECURITY property rather than as a number, because the number alone invites
        // "why not an hour, it is more convenient". ADR 0083's revocation denylist was never built, so there is
        // no way to invalidate an issued access token: signing out clears the cookie and the client's copy
        // while a token already taken keeps working until it expires on its own. This is how long that is.
        var (clientId, secret, _) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
            }));

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var window = TimeSpan.FromSeconds(payload.GetProperty("expires_in").GetInt32());

        Assert.True(window <= TimeSpan.FromMinutes(15),
            $"An issued access token cannot be revoked (ADR 0083's denylist was never built), so this window is "
            + $"the whole exposure after a sign-out or a leak. It is {window.TotalMinutes:N0} minutes. If this "
            + "is deliberate, change ADR 0083 and this test together — do not widen it quietly.");
    }
}
