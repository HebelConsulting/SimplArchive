using System.Net;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// Renewal as the shared library does it for both native apps (ADR 0917): the bearer on every request, a refresh
// shortly before expiry with the HOST's client id, one replay after a 401, and a clean end when renewal cannot work.
// The session store is the host's seam; here the in-memory one.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class RenewalTests : ApiClientSettingsTest
{
    private const string Root = "https://archive.example.test";
    private readonly InMemoryTokenSessionStore _store = new();

    public RenewalTests()
    {
        ApiClientSettings.ClientId = "simplarchive-mobile";
        ApiClientSettings.Sessions = _store;
    }

    private static HttpMessageInvoker Client(TokenSession session, FakeHandler handler) =>
        new(new RenewingAuthHandler(Root, new TokenSession.Holder(session), handler));

    private static HttpRequestMessage Get() => new(HttpMethod.Get, $"{Root}/api");

    private static HttpResponseMessage Tokens(string access, string refresh) =>
        FakeHandler.Json($$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":900}""");

    [Fact]
    public async Task Every_request_carries_the_sessions_bearer()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Client(new TokenSession("access-1", null, DateTimeOffset.MaxValue), handler).SendAsync(Get(), default);

        Assert.Equal("Bearer access-1", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task A_token_about_to_expire_is_renewed_first_with_the_hosts_client_id_and_stored()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/connect/token" ? Tokens("access-2", "refresh-2") : new HttpResponseMessage(HttpStatusCode.OK));

        await Client(new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(10)), handler).SendAsync(Get(), default);

        var (method, uri, _, body) = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal($"{Root}/connect/token", uri.ToString());
        Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", body, StringComparison.Ordinal);
        Assert.Contains("client_id=simplarchive-mobile", body, StringComparison.Ordinal);
        Assert.Equal("Bearer access-2", handler.Requests[1].Authorization);
        Assert.Equal("refresh-2", _store.For(Root)?.RefreshToken);   // rotated, and persisted through the host's seam
    }

    [Fact]
    public async Task A_401_is_renewed_and_the_request_replayed_once()
    {
        var calls = 0;
        var handler = new FakeHandler(r =>
            r.RequestUri!.AbsolutePath == "/connect/token" ? Tokens("access-2", "refresh-2")
            : ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK));

        var response = await Client(new TokenSession("access-1", "refresh-1", DateTimeOffset.MaxValue), handler).SendAsync(Get(), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer access-1", null, "Bearer access-2"], handler.Requests.Select(r => r.Authorization));
    }

    [Fact]
    public async Task A_401_with_no_way_to_renew_ends_the_session_and_forgets_it()
    {
        _store.Set(Root, new TokenSession("access-1", null, DateTimeOffset.MaxValue));
        string? ended = null;
        void OnEnded(string root) => ended = root;
        RenewingAuthHandler.SessionEnded += OnEnded;
        try
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var thrown = await Assert.ThrowsAsync<SessionEndedException>(() =>
                Client(new TokenSession("access-1", null, DateTimeOffset.MaxValue), handler).SendAsync(Get(), default));

            Assert.Equal(Root, thrown.ApiRootUrl);
            Assert.Equal(Root, ended);
            Assert.Null(_store.For(Root));
        }
        finally
        {
            RenewingAuthHandler.SessionEnded -= OnEnded;
        }
    }

    [Fact]
    public async Task A_refused_renewal_ends_the_session_rather_than_retrying_for_ever()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/connect/token"
            ? FakeHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<SessionEndedException>(() =>
            Client(new TokenSession("access-1", "refresh-1", DateTimeOffset.MaxValue), handler).SendAsync(Get(), default));

        Assert.Equal(1, handler.Requests.Count(r => r.Uri.AbsolutePath == "/connect/token"));
    }

    [Fact]
    public void A_session_says_when_it_needs_renewing_and_whether_it_can()
    {
        Assert.True(new TokenSession("a", "r", DateTimeOffset.UtcNow.AddSeconds(30)).NeedsRenewal);   // inside the minute ahead
        Assert.False(new TokenSession("a", "r", DateTimeOffset.UtcNow.AddHours(1)).NeedsRenewal);
        Assert.False(new TokenSession("a", null, DateTimeOffset.UtcNow).CanRenew);
        Assert.True(new TokenSession("a", "r", DateTimeOffset.UtcNow).CanRenew);
    }

    [Fact]
    public void The_in_memory_store_keys_sessions_by_installation_ignoring_case()
    {
        var store = new InMemoryTokenSessionStore();
        var session = new TokenSession("a", "r", DateTimeOffset.MaxValue);

        store.Set("https://Archive.Example.test", session);

        Assert.Same(session, store.For("https://archive.example.test"));
        store.Clear("https://ARCHIVE.example.test");
        Assert.Null(store.For("https://archive.example.test"));
    }
}
