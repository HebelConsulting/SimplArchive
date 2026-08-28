using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for the per-document chat thread (issue #382 — formerly "comments") over the real API + Postgres:
// post → list → reply, and the `chat` link rel that makes the route discoverable.
//
// The thread had no API test of its own before the rename, which is precisely why moving the route was riskier
// than it should have been. These cover the renamed surface: the /chat route, the `messages` payload, and
// `parentMessageId`.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class DocumentChatApiTests
{
    private readonly E2EApiFactory _factory;

    public DocumentChatApiTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Chat_thread_round_trips_with_replies_and_validation()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Chat {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "chat-target" })).GetProperty("id").GetGuid();

        var chatUrl = $"/api/documents/{docId}/chat";

        // Post a top-level message, then a reply to it.
        var top = await PostJson(api, chatUrl, new { body = "Top level" });
        var topId = top.GetProperty("id").GetGuid();
        Assert.Equal("Top level", top.GetProperty("body").GetString());

        await PostJson(api, chatUrl, new { body = "A reply", parentMessageId = topId });

        // The list carries both, under `messages`, with the reply pointing at its parent.
        var list = await GetJson(api, chatUrl);
        var messages = list.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);

        var reply = Assert.Single(messages, m => m.GetProperty("body").GetString() == "A reply");
        Assert.Equal(topId, reply.GetProperty("parentMessageId").GetGuid());
        Assert.Equal(JsonValueKind.Null, Assert.Single(messages, m => m.GetProperty("body").GetString() == "Top level").GetProperty("parentMessageId").ValueKind);

        // Validation: an empty body, and a reply whose parent is itself a reply (the thread stays two deep).
        await AssertBadRequest(api, chatUrl, new { body = "   " }, "EMPTY_CHAT_MESSAGE");
        await AssertBadRequest(api, chatUrl, new { body = "nested", parentMessageId = reply.GetProperty("id").GetGuid() }, "INVALID_PARENT_CHAT_MESSAGE");
    }

    // ADR 0543: a client must be able to REACH the thread by following a rel, not by composing the URL. If this
    // rel disappears, the clients silently fall back to composing — which is the failure this ADR exists to stop.
    [Fact]
    public async Task Document_and_its_folder_listing_both_advertise_the_chat_rel()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Chat rel {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "rel-target" })).GetProperty("id").GetGuid();

        var document = await GetJson(api, $"/api/documents/{docId}");
        var documentRel = ChatHref(document);
        Assert.NotNull(documentRel);

        // The folder listing is where a browsing client meets a document, so it has to carry the rel too —
        // otherwise the client has nothing to follow until it fetches each document separately.
        var child = Assert.Single(
            (await GetJson(api, $"/api/documents/{repoId}/children")).GetProperty("children").EnumerateArray().ToList(),
            c => c.GetProperty("id").GetGuid() == docId);
        Assert.Equal(documentRel, ChatHref(child));

        // And the advertised href is the one that actually serves the thread.
        (await api.GetAsync(documentRel)).EnsureSuccessStatusCode();
    }

    private static string? ChatHref(JsonElement resource) =>
        resource.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == "chat") is { ValueKind: JsonValueKind.Object } link
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

    private static async Task AssertBadRequest(HttpClient api, string url, object body, string expectedErrorCode)
    {
        var response = await api.PostAsJsonAsync(url, body);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedErrorCode, problem.GetProperty("errorCode").GetString());
    }
}
