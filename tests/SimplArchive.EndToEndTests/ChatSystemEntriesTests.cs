using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for the automatic chat entries (ADR 0545): filing a document, saving a version, and making an older
// version current again each leave a record in the thread.
//
// The assertions worth reading are the ones about what is NOT stored: a system entry has an empty body, because
// its wording is a localized template the clients render, and its "Version N" and check-in comment are read from
// the referenced version at request time rather than copied at post time.
[Collection(E2ECollection.Name)]
public class ChatSystemEntriesTests
{
    private readonly E2EApiFactory _factory;

    public ChatSystemEntriesTests(E2EApiFactory factory) => _factory = factory;

    private const int UserPost = 0, DocumentFiled = 1, VersionFiled = 2, VersionActivated = 3;

    [Fact]
    public async Task Filing_a_document_and_its_versions_records_entries_in_the_thread()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Entries {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "filed-doc" })).GetProperty("id").GetGuid();

        // First version: the document itself arrives, AND it gets the same per-version entry every version gets.
        var v1 = await ConfirmVersionAsync(api, docId, "first draft");

        var afterFirst = await MessagesAsync(api, docId);
        Assert.Equal(2, afterFirst.Count);
        Assert.Equal(DocumentFiled, afterFirst[0].GetProperty("kind").GetInt32());
        Assert.Equal(VersionFiled, afterFirst[1].GetProperty("kind").GetInt32());

        // A system entry stores NO text — its wording is a localized template, not English in the database.
        Assert.Equal("", afterFirst[0].GetProperty("body").GetString());
        Assert.Equal("", afterFirst[1].GetProperty("body").GetString());

        // The version entry carries the number and comment, read from the version itself.
        Assert.Equal(1, afterFirst[1].GetProperty("versionNumber").GetInt32());
        Assert.Equal("first draft", afterFirst[1].GetProperty("versionComment").GetString());

        // A "document filed" entry is about the document, so it names no version.
        Assert.Equal(JsonValueKind.Null, afterFirst[0].GetProperty("versionNumber").ValueKind);

        // A second version adds ONE entry — the document was already filed.
        await ConfirmVersionAsync(api, docId, "second pass");
        var afterSecond = await MessagesAsync(api, docId);
        Assert.Equal(3, afterSecond.Count);
        Assert.Equal(VersionFiled, afterSecond[2].GetProperty("kind").GetInt32());
        Assert.Equal(2, afterSecond[2].GetProperty("versionNumber").GetInt32());
        Assert.Equal("second pass", afterSecond[2].GetProperty("versionComment").GetString());

        // Making the older version current again is recorded: it changes what everyone sees without adding
        // anything, which is exactly why it should not be silent.
        (await api.PostAsync($"/api/documents/{docId}/versions/{v1}/restore", null)).EnsureSuccessStatusCode();

        var afterRestore = await MessagesAsync(api, docId);
        Assert.Equal(4, afterRestore.Count);
        Assert.Equal(VersionActivated, afterRestore[3].GetProperty("kind").GetInt32());
        Assert.Equal(1, afterRestore[3].GetProperty("versionNumber").GetInt32());

        // Re-pinning the version that is already current is a no-op, so it must not post again.
        (await api.PostAsync($"/api/documents/{docId}/versions/{v1}/restore", null)).EnsureSuccessStatusCode();
        Assert.Equal(4, (await MessagesAsync(api, docId)).Count);
    }

    // The version entry's comment is READ FROM THE VERSION, not copied onto the message. There is no endpoint
    // to edit a version comment today (it can only be set at create or finalize, and finalize won't overwrite
    // one), so this asserts the weaker observable form: the thread and the version resource always agree. The
    // stronger property — that a later edit shows through instead of leaving a stale copy — follows from the
    // projection reading the version, and becomes testable the day such an endpoint exists (ADR 0545).
    [Fact]
    public async Task The_version_entrys_comment_agrees_with_the_version_itself()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Agree {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "agreeing-comment" })).GetProperty("id").GetGuid();
        var versionId = await ConfirmVersionAsync(api, docId, "reviewed by legal");

        var fromThread = (await MessagesAsync(api, docId)).Single(m => m.GetProperty("kind").GetInt32() == VersionFiled);
        var fromVersion = await GetJson(api, $"/api/documents/{docId}/versions/{versionId}");

        Assert.Equal(fromVersion.GetProperty("comment").GetString(), fromThread.GetProperty("versionComment").GetString());
        Assert.Equal("reviewed by legal", fromThread.GetProperty("versionComment").GetString());
    }

    // A version with no comment still gets its entry — the entry announces the version, and the comment is an
    // optional extra line beneath "Version N".
    [Fact]
    public async Task A_version_without_a_comment_still_gets_an_entry()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Bare {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "no-comment" })).GetProperty("id").GetGuid();
        await ConfirmVersionAsync(api, docId, comment: null);

        var entry = (await MessagesAsync(api, docId)).Single(m => m.GetProperty("kind").GetInt32() == VersionFiled);
        Assert.Equal(1, entry.GetProperty("versionNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("versionComment").ValueKind);
    }

    // A message a person types stays a UserPost with its own text and no version — the system kinds are not
    // something a client can create.
    [Fact]
    public async Task A_typed_message_is_a_user_post()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Typed {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var posted = await PostJson(api, $"/api/documents/{repoId}/chat", new { body = "a human wrote this" });

        Assert.Equal(UserPost, posted.GetProperty("kind").GetInt32());
        Assert.Equal("a human wrote this", posted.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, posted.GetProperty("versionNumber").ValueKind);
    }

    private async Task<Guid> ConfirmVersionAsync(HttpClient api, Guid docId, string? comment)
    {
        var version = await PostJson(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt", comment });
        var versionId = version.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes($"body {Guid.NewGuid():N}")))).EnsureSuccessStatusCode();
        }

        var confirm = await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { });
        confirm.EnsureSuccessStatusCode();
        return versionId;
    }

    private static async Task<List<JsonElement>> MessagesAsync(HttpClient api, Guid docId) =>
        (await GetJson(api, $"/api/documents/{docId}/chat")).GetProperty("messages").EnumerateArray().ToList();

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
