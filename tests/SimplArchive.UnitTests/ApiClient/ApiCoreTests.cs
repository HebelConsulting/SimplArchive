using Microsoft.Extensions.Logging;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// ApiCore against a real loopback server, because it builds its own handler stack (ADR 0917): one root read for
// many rels (ADR 0557), a failed root that says so instead of looking empty (#1077), paging that follows `next`, and
// sign-out revoking both tokens with the HOST's client id (ADR 0895).
[Collection(ApiClientSettingsCollection.Name)]
public sealed class ApiCoreTests : ApiClientSettingsTest
{
    private const string RootJson = """{"links":[{"rel":"repositories","href":"/api/repositories"},{"rel":"me","href":"/api/me"}]}""";

    private static ApiCore CoreAt(LoopbackServer server)
    {
        ApiClientSettings.ApiRootUrl = server.BaseUrl;
        return new ApiCore("access-1");
    }

    [Fact]
    public async Task The_root_is_read_once_however_many_rels_are_followed()
    {
        using var server = new LoopbackServer((_, path, _) => path == "/api" ? (200, RootJson) : (404, "{}"));
        var core = CoreAt(server);

        Assert.Equal("api/repositories", await core.RootHrefAsync("repositories"));
        Assert.Equal("api/me", await core.RootHrefAsync("me"));
        Assert.Equal("api/repositories", await core.RootHrefAsync("repositories"));

        Assert.Single(server.Requests, r => r.Path == "/api");
    }

    [Fact]
    public async Task A_rel_the_root_does_not_advertise_is_named()
    {
        using var server = new LoopbackServer((_, _, _) => (200, RootJson));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => CoreAt(server).RootHrefAsync("admin"));

        Assert.Contains("'admin'", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_root_read_is_a_failure_not_an_empty_set_of_rels()
    {
        using var server = new LoopbackServer((_, _, _) => (500, """{"title":"boom"}"""));

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => CoreAt(server).GetRootLinksAsync());

        Assert.Contains("500", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_follows_next_until_there_is_none()
    {
        using var server = new LoopbackServer((_, path, _) => path switch
        {
            "/api/items" => (200, """{"items":[{"n":1},{"n":2}],"links":[{"rel":"next","href":"api/items?cursor=c2"}]}"""),
            "/api/items?cursor=c2" => (200, """{"items":[{"n":3}],"links":[]}"""),
            _ => (404, "{}"),
        });

        var items = await CoreAt(server).LoadPagedAsync("api/items", "items", e => e.GetProperty("n").GetInt32(), default);

        Assert.Equal([1, 2, 3], items);
    }

    [Fact]
    public async Task Requests_carry_the_bearer_the_core_was_built_with()
    {
        using var server = new LoopbackServer((_, _, _) => (200, RootJson));
        var core = CoreAt(server);

        await core.GetRootLinksAsync();

        Assert.Equal("access-1", core.AccessToken);
    }

    [Fact]
    public async Task Sign_out_revokes_both_tokens_with_the_hosts_client_id_and_forgets_the_session()
    {
        using var server = new LoopbackServer((_, _, _) => (200, "{}"));
        ApiClientSettings.ApiRootUrl = server.BaseUrl;
        ApiClientSettings.ClientId = "simplarchive-mobile";
        var store = new InMemoryTokenSessionStore();
        ApiClientSettings.Sessions = store;
        store.Set(server.BaseUrl, new TokenSession("access-1", "refresh-1", DateTimeOffset.MaxValue));

        await new ApiCore("access-1").RevokeSessionAsync();

        var revokes = server.Requests.Where(r => r.Path == "/connect/revoke").ToList();
        Assert.Equal(2, revokes.Count);
        Assert.Contains(revokes, r => r.Body.Contains("token=refresh-1", StringComparison.Ordinal) && r.Body.Contains("token_type_hint=refresh_token", StringComparison.Ordinal));
        Assert.Contains(revokes, r => r.Body.Contains("token=access-1", StringComparison.Ordinal) && r.Body.Contains("token_type_hint=access_token", StringComparison.Ordinal));
        Assert.All(revokes, r => Assert.Contains("client_id=simplarchive-mobile", r.Body, StringComparison.Ordinal));
        Assert.Null(store.For(server.BaseUrl));
    }

    [Fact]
    public async Task A_revocation_the_server_refuses_is_logged_as_a_warning_without_the_token()
    {
        using var server = new LoopbackServer((_, _, _) => (503, "{}"));
        ApiClientSettings.ApiRootUrl = server.BaseUrl;
        var logger = new RecordingLogger();
        ApiClientSettings.Logger = logger;

        await new ApiCore("secret-access-token").RevokeSessionAsync();   // best effort: sign-out still completes

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("503", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-access-token", warning.Message, StringComparison.Ordinal);
    }
}
