using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop's session bookkeeping. Pure view-of-the-model tests: what can go wrong here is which token gets
// sent and which server it belongs to, and both are decidable without a server or a window.
//
// The bugs these exist to prevent were all found the expensive way — 115 desktop tests failing with 401 because
// no session was recorded, then two failing because two users shared one.
public class DesktopTokenSessionTests
{
    private static TokenSessions Fresh()
    {
        // Never the real Keychain: a test that writes to the developer's login keychain is a test that leaves
        // something behind, and one that reads it is a test whose result depends on the machine.
        SecretStores.Override = new InMemorySecretStore();
        return new TokenSessions();
    }

    [Fact]
    public void A_session_is_kept_per_server_so_two_environments_never_share_a_token()
    {
        var sessions = Fresh();
        sessions.Set("https://prod.example", new TokenSession("prod-access", "prod-refresh", DateTimeOffset.UtcNow.AddHours(1)));
        sessions.Set("https://test.example", new TokenSession("test-access", "test-refresh", DateTimeOffset.UtcNow.AddHours(1)));

        // The whole point of keying by server: signing into one deployment must not decide who you are on
        // another. One slot for all of them means whichever was signed into last silently wins.
        Assert.Equal("prod-access", sessions.For("https://prod.example")!.AccessToken);
        Assert.Equal("test-access", sessions.For("https://test.example")!.AccessToken);
    }

    [Fact]
    public void A_trailing_slash_does_not_split_one_server_into_two()
    {
        var sessions = Fresh();
        sessions.Set("https://one.example/", new TokenSession("a", "r", DateTimeOffset.UtcNow.AddHours(1)));

        // The same server typed two ways is the same server. Otherwise a profile edited to add a slash silently
        // becomes a second, tokenless environment.
        Assert.NotNull(sessions.For("https://one.example"));
        Assert.Equal("a", sessions.For("https://one.example")!.AccessToken);
    }

    [Fact]
    public void Only_the_refresh_token_is_persisted_and_a_restored_session_asks_to_be_renewed()
    {
        var store = new InMemorySecretStore();
        SecretStores.Override = store;

        var sessions = new TokenSessions();
        sessions.Set("https://one.example", new TokenSession("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));

        // The ACCESS token is not persisted — it would be stale before it was ever read again, and keeping it
        // would leave a credential lying about for no benefit.
        Assert.Equal("refresh", store.Read("https://one.example"));
        Assert.DoesNotContain("access", store.Read("https://one.example"));

        // A fresh process restores from the store: no access token, an expiry already past, so it reads as
        // "needs renewal" with no special case and the first request exchanges the refresh token.
        var restored = new TokenSessions().For("https://one.example");
        Assert.NotNull(restored);
        Assert.Equal(string.Empty, restored!.AccessToken);
        Assert.True(restored.NeedsRenewal);
        Assert.True(restored.CanRenew);
    }

    [Fact]
    public void A_refused_session_is_forgotten_in_the_store_too()
    {
        var store = new InMemorySecretStore();
        SecretStores.Override = store;

        var sessions = new TokenSessions();
        sessions.Set("https://one.example", new TokenSession("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        sessions.Clear("https://one.example");

        // A refresh token the server has refused is worthless. Leaving it would make every later launch begin
        // with a failed renewal before falling back to the logon window — a slow way to say "signed out".
        Assert.Null(store.Read("https://one.example"));
        Assert.Null(sessions.For("https://one.example"));
    }

    [Fact]
    public void A_session_with_no_refresh_token_never_claims_it_can_renew()
    {
        // What a client built from a bare access token holds — impersonation, and every test. It must send the
        // token and NOT attempt a renewal it cannot perform.
        var session = new TokenSession("access", null, DateTimeOffset.MaxValue);

        Assert.False(session.CanRenew);
        Assert.False(session.NeedsRenewal);
    }

    [Fact]
    public void Renewal_is_due_before_expiry_not_at_it()
    {
        // Renewing exactly at expiry guarantees a population of requests that leave valid and arrive expired.
        var nearlyDue = new TokenSession("a", "r", DateTimeOffset.UtcNow + TokenSession.RenewAhead + TimeSpan.FromSeconds(30));
        var due = new TokenSession("a", "r", DateTimeOffset.UtcNow + TokenSession.RenewAhead - TimeSpan.FromSeconds(5));

        Assert.False(nearlyDue.NeedsRenewal);
        Assert.True(due.NeedsRenewal);
    }
}
