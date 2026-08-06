using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for the tenant-visible identity card (ADR 0544): the small "who is this?" panel behind an author
// name in the chat thread. Display name, email and photo are readable by ANY member of the tenant — a deliberate
// widening, since gating them to administrators made the card useless for the people who actually read threads.
//
// The security-relevant assertions are the boundary ones: a card resolves only inside the caller's own tenant,
// and the card resource exposes nothing beyond the four fields it needs (no rights flags, no MFA state).
[Collection(E2ECollection.Name)]
public class UserCardApiTests
{
    private readonly E2EApiFactory _factory;

    public UserCardApiTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_ordinary_member_can_read_a_colleagues_card()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);

        var colleagueEmail = $"card-colleague-{Guid.NewGuid():N}@e2e.local";
        var colleagueId = await _factory.SeedUserAsync(tenantId, colleagueEmail, "card1234", "Card Colleague");

        // A plain member: no CanManageUsers, which is exactly the caller the old gating locked out.
        var readerEmail = $"card-reader-{Guid.NewGuid():N}@e2e.local";
        const string password = "card1234";
        await _factory.SeedUserAsync(tenantId, readerEmail, password, "Card Reader");
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(readerEmail, password));

        var card = await GetJson(reader, $"/api/users/{colleagueId}/card");

        Assert.Equal(colleagueId, card.GetProperty("userId").GetGuid());
        Assert.Equal("Card Colleague", card.GetProperty("displayName").GetString());
        Assert.Equal(colleagueEmail, card.GetProperty("email").GetString());
        Assert.True(card.GetProperty("isActive").GetBoolean());
        Assert.False(card.GetProperty("hasPhoto").GetBoolean());

        // No photo → no photo rel. Absence of the rel is the answer, so a client never probes for a 404 (ADR 0543).
        Assert.Null(Rel(card, "photo"));
        Assert.NotNull(Rel(card, "self"));

        // The card is a deliberately small projection — none of the administrative user fields ride along.
        foreach (var leaked in new[] { "canManageUsers", "isTenantAdmin", "mfaEnabled", "passwordHash", "clearanceRank" })
        {
            Assert.False(card.TryGetProperty(leaked, out _), $"the card must not expose '{leaked}'");
        }
    }

    // The tenant query filter is the boundary — there is no explicit tenant check in the action, so this test is
    // what proves the filter actually carries it.
    [Fact]
    public async Task A_card_from_another_tenant_is_not_found()
    {
        var (_, _, tenantA) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var (_, _, tenantB) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var strangerId = await _factory.SeedUserAsync(tenantB, $"card-stranger-{Guid.NewGuid():N}@e2e.local", "card1234", "Other Tenant");

        var readerEmail = $"card-outsider-{Guid.NewGuid():N}@e2e.local";
        const string password = "card1234";
        await _factory.SeedUserAsync(tenantA, readerEmail, password, "Outsider");
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(readerEmail, password));

        var response = await reader.GetAsync($"/api/users/{strangerId}/card");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The photo route was widened too, so it needs the same boundary.
        var photo = await reader.GetAsync($"/api/users/{strangerId}/photo");
        Assert.Equal(HttpStatusCode.NotFound, photo.StatusCode);
    }

    // Standing convention: every GET action has a companion HEAD.
    [Fact]
    public async Task Head_mirrors_get_for_a_card()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var userId = await _factory.SeedUserAsync(tenantId, $"card-head-{Guid.NewGuid():N}@e2e.local", "card1234", "Head Card");

        var readerEmail = $"card-head-reader-{Guid.NewGuid():N}@e2e.local";
        const string password = "card1234";
        await _factory.SeedUserAsync(tenantId, readerEmail, password, "Head Reader");
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(readerEmail, password));

        var head = await reader.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/users/{userId}/card"));
        Assert.Equal(HttpStatusCode.NoContent, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        var missing = await reader.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/users/{Guid.NewGuid()}/card"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // The chat thread is what the card exists for: a message has to carry enough to reach its author's card, and
    // must do it by REL rather than by a URL the client rebuilds (ADR 0543).
    [Fact]
    public async Task A_chat_message_advertises_its_authors_card()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var authorEmail = $"card-author-{Guid.NewGuid():N}@e2e.local";
        const string password = "card1234";
        var authorId = await _factory.SeedUserAsync(tenantId, authorEmail, password, "Chat Author", canManageRepositories: true);
        using var author = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(authorEmail, password));

        var repoId = (await PostJson(author, "/api/repositories", new { name = $"Card {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var posted = await PostJson(author, $"/api/documents/{repoId}/chat", new { body = "Who wrote this?" });

        // The author label is the person's NAME — it used to render their raw email address.
        Assert.Equal("Chat Author", posted.GetProperty("authorName").GetString());
        Assert.Equal(authorId, posted.GetProperty("authorUserId").GetGuid());

        var cardHref = Rel(posted, "author-card");
        Assert.NotNull(cardHref);

        // Following the advertised href reaches the author's real card.
        var card = await GetJson(author, cardHref!);
        Assert.Equal(authorId, card.GetProperty("userId").GetGuid());
        Assert.Equal("Chat Author", card.GetProperty("displayName").GetString());

        // Same shape when the thread is listed, not just on the create response.
        var listed = (await GetJson(author, $"/api/documents/{repoId}/chat")).GetProperty("messages").EnumerateArray().Single();
        Assert.Equal(cardHref, Rel(listed, "author-card"));
    }

    // A ServiceAccount is an automation, not a person: no card, and the MISSING rel is how a client knows to
    // render the name as plain text instead of a link (ADR 0544).
    [Fact]
    public async Task A_service_account_authored_message_advertises_no_card()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Card svc {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var posted = await PostJson(api, $"/api/documents/{repoId}/chat", new { body = "Automated note" });

        Assert.Equal(JsonValueKind.Null, posted.GetProperty("authorUserId").ValueKind);
        Assert.Null(Rel(posted, "author-card"));
    }

    private static string? Rel(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == rel) is { ValueKind: JsonValueKind.Object } link
            ? link.GetProperty("href").GetString()
            : null;

    private static async Task<JsonElement> PostJson(HttpClient api, string url, object body)
    {
        var response = await api.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> GetJson(HttpClient api, string url)
    {
        var response = await api.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }
}
