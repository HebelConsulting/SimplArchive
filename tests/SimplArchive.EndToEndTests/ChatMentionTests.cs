using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// @-mentions over the real API + Postgres (issue #383).
//
// The case that matters most here is the REJECTION: a mention subscribes the named user to the document and
// sends them a notification carrying its name, so accepting one for somebody who cannot see the document would
// hand them both. The picker never offers such a user, which is exactly why the server must not rely on it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ChatMentionTests
{
    private readonly E2EApiFactory _factory;

    public ChatMentionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_mention_resolves_to_a_name_and_subscribes_the_person_named()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Mentions {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "mention-target" })).GetProperty("id").GetGuid();

        // A user who can see the document: granted CanSee on the repository, which the document inherits.
        var email = $"mentioned-{Guid.NewGuid():N}@e2e.local";
        const string password = "mention1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Mentioned Person");
        await PutJson(api, $"/api/documents/{repoId}/acl-entries/users/{userId}", new { canSee = true, canRead = true });

        var chatUrl = $"/api/documents/{docId}/chat";
        var posted = await PostJson(api, chatUrl, new { body = $"Please review @[{userId}]" });

        // The BODY keeps the id — a display name is neither unique nor stable — and the name travels beside it.
        Assert.Contains($"@[{userId}]", posted.GetProperty("body").GetString());
        var mention = Assert.Single(posted.GetProperty("mentions").EnumerateArray().ToList());
        Assert.Equal(userId, mention.GetProperty("userId").GetGuid());
        Assert.Equal("Mentioned Person", mention.GetProperty("displayName").GetString());

        // The listing resolves it the same way, so a client rendering the thread never has to look a name up.
        var listed = Assert.Single((await GetJson(api, chatUrl)).GetProperty("messages").EnumerateArray().ToList());
        Assert.Equal("Mentioned Person", listed.GetProperty("mentions").EnumerateArray().Single().GetProperty("displayName").GetString());

        // Being addressed subscribes you, so the answers reach you too.
        using var mentioned = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        Assert.True((await GetJson(mentioned, $"/api/documents/{docId}/subscription")).GetProperty("subscribed").GetBoolean());
    }

    [Fact]
    public async Task Mentioning_someone_who_cannot_see_the_document_is_refused()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Mentions denied {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "private-target" })).GetProperty("id").GetGuid();

        // Seeded, but granted nothing on this repository.
        var outsiderId = await _factory.SeedUserAsync(tenantId, $"outsider-{Guid.NewGuid():N}@e2e.local", "outsider1234", "Outsider");

        var response = await api.PostAsJsonAsync($"/api/documents/{docId}/chat", new { body = $"psst @[{outsiderId}]" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_CHAT_MENTION", problem.GetProperty("errorCode").GetString());

        // And nothing was written: the whole message is refused, not just the mention stripped out of it.
        Assert.Empty((await GetJson(api, $"/api/documents/{docId}/chat")).GetProperty("messages").EnumerateArray().ToList());
    }

    [Fact]
    public async Task The_picker_offers_only_users_who_can_see_the_document()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Picker {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "picker-target" })).GetProperty("id").GetGuid();

        var marker = Guid.NewGuid().ToString("N")[..8];
        var insiderId = await _factory.SeedUserAsync(tenantId, $"in-{marker}@e2e.local", "picker1234", $"Insider {marker}");
        var outsiderId = await _factory.SeedUserAsync(tenantId, $"out-{marker}@e2e.local", "picker1234", $"Outsider {marker}");
        await PutJson(api, $"/api/documents/{repoId}/acl-entries/users/{insiderId}", new { canSee = true, canRead = true });

        // The thread advertises the picker rather than the client composing its URL (ADR 0543).
        var thread = await GetJson(api, $"/api/documents/{docId}/chat");
        var href = thread.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "mentionable-users")
            .GetProperty("href").GetString()!;

        var offered = (await GetJson(api, $"{href}?q={marker}")).GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(insiderId, offered);
        // The whole point: the picker is not a staff directory. Somebody with no access to this document is not
        // offered, so their name never leaks to a caller who merely holds CanSee here.
        Assert.DoesNotContain(outsiderId, offered);
    }

    private static async Task<JsonElement> PostJson(HttpClient api, string url, object body)
    {
        var response = await api.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task PutJson(HttpClient api, string url, object body) =>
        (await api.PutAsJsonAsync(url, body)).EnsureSuccessStatusCode();

    private static async Task<JsonElement> GetJson(HttpClient api, string url)
    {
        var response = await api.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }
}
