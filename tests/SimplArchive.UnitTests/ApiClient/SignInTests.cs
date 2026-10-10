using System.Text;
using System.Text.Json;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// The sign-in steps every native app shares (ADR 0917, for the mobile app's ADR 0916): discovery, PKCE, the
// authorize address, the state check and the code exchange, against a loopback server; and the server check a host
// runs before it signs in at all.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class SignInTests : ApiClientSettingsTest
{
    private const string Redirect = "dev.simplarchive.mobile:/oauth2redirect";

    private static string Jwt(string payload) =>
        $"e30.{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.sig";

    /// <summary>An identity server on a loopback port whose discovery document names its own token endpoint.</summary>
    private static LoopbackServer IdentityServer()
    {
        var self = string.Empty;
        var server = new LoopbackServer((_, path, _) => path switch
        {
            "/.well-known/openid-configuration" =>
                (200, $$"""{"authorization_endpoint":"https://id.example.test/connect/authorize","token_endpoint":"{{self}}connect/token"}"""),
            "/connect/token" => (200, $$"""{"access_token":"a1","refresh_token":"r1","expires_in":900,"id_token":"{{Jwt("""{"email":"anna@demo.test"}""")}}"}"""),
            _ => (404, "{}"),
        });
        self = server.BaseUrl;
        return server;
    }

    [Fact]
    public async Task The_authorize_address_carries_the_client_redirect_s256_challenge_state_and_the_options()
    {
        using var server = IdentityServer();

        var signIn = await PkceSignIn.StartAsync(server.BaseUrl, "simplarchive-mobile", Redirect, "openid offline_access",
            forceLogin: true, loginHint: "anna@demo.test");

        var uri = new Uri(signIn.AuthorizeUrl);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal("https://id.example.test/connect/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("simplarchive-mobile", query["client_id"]);
        Assert.Equal(Redirect, query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("openid offline_access", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(43, query["code_challenge"].ToString().Length);   // base64url SHA-256, no padding
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.Equal("login", query["prompt"]);
        Assert.Equal("anna@demo.test", query["login_hint"]);
    }

    [Fact]
    public async Task Two_sign_ins_never_share_a_challenge_or_a_state()
    {
        using var server = IdentityServer();

        var first = new Uri((await PkceSignIn.StartAsync(server.BaseUrl, "c", Redirect, "openid")).AuthorizeUrl).Query;
        var second = new Uri((await PkceSignIn.StartAsync(server.BaseUrl, "c", Redirect, "openid")).AuthorizeUrl).Query;

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("prompt=", first, StringComparison.Ordinal);
        Assert.DoesNotContain("login_hint=", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_with_another_state_or_no_code_is_ignored_and_nothing_is_exchanged()
    {
        using var server = IdentityServer();
        var signIn = await PkceSignIn.StartAsync(server.BaseUrl, "c", Redirect, "openid");

        Assert.Null(await signIn.CompleteAsync("code-1", "someone-elses-state"));
        Assert.Null(await signIn.CompleteAsync(null, StateOf(signIn)));
        Assert.DoesNotContain(server.Requests, r => r.Path == "/connect/token");
    }

    [Fact]
    public async Task The_code_is_exchanged_with_the_verifier_and_the_same_redirect_and_the_email_read_from_the_id_token()
    {
        using var server = IdentityServer();
        var signIn = await PkceSignIn.StartAsync(server.BaseUrl, "simplarchive-mobile", Redirect, "openid offline_access");

        var result = await signIn.CompleteAsync("code-1", StateOf(signIn));

        Assert.NotNull(result);
        Assert.Equal("a1", result!.AccessToken);
        Assert.Equal("r1", result.RefreshToken);
        Assert.Equal("anna@demo.test", result.Email);
        Assert.InRange(result.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        var exchange = Assert.Single(server.Requests, r => r.Path == "/connect/token").Body;
        Assert.Contains("grant_type=authorization_code", exchange, StringComparison.Ordinal);
        Assert.Contains("code=code-1", exchange, StringComparison.Ordinal);
        Assert.Contains($"redirect_uri={Uri.EscapeDataString(Redirect)}", exchange, StringComparison.Ordinal);
        Assert.Contains("client_id=simplarchive-mobile", exchange, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", exchange, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_response_without_a_lifetime_counts_as_already_expired_so_the_first_request_renews()
    {
        var now = DateTimeOffset.UtcNow;

        var result = PkceSignIn.Parse(JsonSerializer.Deserialize<JsonElement>("""{"access_token":"a"}"""), now);

        Assert.Equal(now, result.ExpiresAt);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    [InlineData("a.%%%.c")]
    public void A_malformed_id_token_has_no_email_rather_than_failing_the_sign_in(string? jwt) =>
        Assert.Null(PkceSignIn.ReadEmailFromJwt(jwt));

    [Fact]
    public void An_address_is_SimplArchive_only_when_its_root_advertises_itself_the_repositories_and_sign_in()
    {
        Assert.True(ServerIdentity.LooksLikeApiRoot("""{"links":[{"rel":"self","href":"/api"},{"rel":"repositories","href":"/api/repositories"},{"rel":"openIdConfiguration","href":"/.well-known/openid-configuration"}]}"""));
        Assert.False(ServerIdentity.LooksLikeApiRoot("""{"links":[{"rel":"self","href":"/api"},{"rel":"repositories","href":"/api/repositories"}]}"""));
        Assert.False(ServerIdentity.LooksLikeApiRoot("<html>a router's login page</html>"));
    }

    [Fact]
    public async Task An_address_that_does_not_answer_like_SimplArchive_is_not_one()
    {
        using var server = new LoopbackServer((_, _, _) => (200, """{"hello":"world"}"""));

        Assert.False(await ServerIdentity.IsSimplArchiveAsync(server.BaseUrl));
        Assert.False(await ServerIdentity.IsSimplArchiveAsync("http://127.0.0.1:1"));   // nothing listening
    }

    private static string StateOf(PkceSignIn signIn) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(signIn.AuthorizeUrl).Query)["state"]!;

    // The check says WHY (a phone with its network permission off read "No connection" while the server was fine).
    [Fact]
    public async Task The_check_names_found_not_simplarchive_and_unreachable()
    {
        using var root = new LoopbackServer((_, _, _) => (200, """{"links":[{"rel":"self","href":"/api"},{"rel":"repositories","href":"/api/repositories"},{"rel":"openIdConfiguration","href":"/.well-known/openid-configuration"}]}"""));
        using var other = new LoopbackServer((_, _, _) => (200, """{"hello":"world"}"""));

        Assert.Equal(ServerCheckOutcome.Found, (await ServerIdentity.CheckAsync(root.BaseUrl)).Outcome);
        Assert.Equal(ServerCheckOutcome.NotSimplArchive, (await ServerIdentity.CheckAsync(other.BaseUrl)).Outcome);
        Assert.Equal(ServerCheckOutcome.Unreachable, (await ServerIdentity.CheckAsync("http://127.0.0.1:1")).Outcome);
    }

    [Fact]
    public void A_denied_network_is_recognised_as_a_socket_error_and_as_androids_text()
    {
        var socket = new HttpRequestException("send failed", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied));
        var android = new HttpRequestException("Connection failure", new IOException("socket failed: EACCES (Permission denied)"));
        var refused = new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));

        Assert.Equal(ServerCheckOutcome.NetworkNotAllowed, ServerIdentity.Classify(socket).Outcome);
        Assert.Equal(ServerCheckOutcome.NetworkNotAllowed, ServerIdentity.Classify(android).Outcome);
        Assert.Equal(ServerCheckOutcome.Unreachable, ServerIdentity.Classify(refused).Outcome);
        Assert.Equal("SmNetworkDenied", ServerIdentity.Classify(android).MessageKey);
    }

    [Fact]
    public async Task A_failed_check_is_logged_with_its_reason()
    {
        var logger = new RecordingLogger();
        ApiClientSettings.Logger = logger;

        await ServerIdentity.CheckAsync("http://127.0.0.1:1");

        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("Unreachable", StringComparison.Ordinal));
    }
}
