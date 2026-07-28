using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for sticky notes / positional annotations (ADR "Document annotations (sticky notes)") over the
// real API + Postgres + MinIO: create → list → edit (author, If-Match) → delete, plus the If-Match
// concurrency contract (428/412) and placement validation (400s).
[Collection(E2ECollection.Name)]
public class DocumentAnnotationApiTests
{
    private readonly E2EApiFactory _factory;

    public DocumentAnnotationApiTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Annotation_crud_round_trip_with_concurrency_and_validation()
    {
        var api = await AuthedClientAsync();
        var (docId, versionId) = await SeedConfirmedDocumentAsync(api);

        var annotationsUrl = $"/api/documents/{docId}/versions/{versionId}/annotations";

        // Create a note.
        var created = await PostJson(api, annotationsUrl, new { pageIndex = 0, positionX = 0.25, positionY = 0.4, text = "Look here", color = "#FFEB3B" });
        var noteId = created.GetProperty("id").GetGuid();
        Assert.Equal("Look here", created.GetProperty("text").GetString());
        Assert.True(created.GetProperty("canEdit").GetBoolean());
        Assert.True(created.GetProperty("canDelete").GetBoolean());
        var etag = created.GetProperty("etag").GetString()!;

        // List shows it (and the author has CanAnnotate → canCreate true, ADR "CanAnnotate right").
        var list = await GetJson(api, annotationsUrl);
        Assert.Equal(1, list.GetProperty("annotations").GetArrayLength());
        Assert.True(list.GetProperty("canCreate").GetBoolean());

        var noteUrl = $"{annotationsUrl}/{noteId}";

        // Edit without If-Match → 428.
        var noMatch = await api.PutAsJsonAsync(noteUrl, new { pageIndex = 0, positionX = 0.3, positionY = 0.5, text = "Moved", color = "#8BC34A" });
        Assert.Equal(HttpStatusCode.PreconditionRequired, noMatch.StatusCode);

        // Edit with a stale If-Match → 412.
        var stale = await PutWithIfMatch(api, noteUrl, Guid.NewGuid().ToString(), new { pageIndex = 0, positionX = 0.3, positionY = 0.5, text = "Moved", color = "#8BC34A" });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        // Edit with the correct If-Match → 200, new position + text + a fresh etag.
        var okResp = await PutWithIfMatch(api, noteUrl, etag, new { pageIndex = 0, positionX = 0.3, positionY = 0.55, text = "Moved note", color = "#8BC34A" });
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        var updated = JsonSerializer.Deserialize<JsonElement>(await okResp.Content.ReadAsStringAsync());
        Assert.Equal("Moved note", updated.GetProperty("text").GetString());
        Assert.Equal(0.55, updated.GetProperty("positionY").GetDouble(), 3);
        var newEtag = updated.GetProperty("etag").GetString()!;
        Assert.NotEqual(etag, newEtag);

        // Delete with the fresh If-Match → 204; the list is empty.
        var del = await DeleteWithIfMatch(api, noteUrl, newEtag);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var afterDelete = await GetJson(api, annotationsUrl);
        Assert.Equal(0, afterDelete.GetProperty("annotations").GetArrayLength());

        // Validation: out-of-range position, bad colour, empty text.
        await AssertBadRequest(api, annotationsUrl, new { pageIndex = 0, positionX = 1.5, positionY = 0.2, text = "x", color = "#FFEB3B" }, "INVALID_ANNOTATION_POSITION");
        await AssertBadRequest(api, annotationsUrl, new { pageIndex = 0, positionX = 0.2, positionY = 0.2, text = "x", color = "red" }, "INVALID_ANNOTATION_COLOR");
        await AssertBadRequest(api, annotationsUrl, new { pageIndex = 0, positionX = 0.2, positionY = 0.2, text = "  ", color = "#FFEB3B" }, "EMPTY_ANNOTATION");
    }

    [Fact]
    public async Task Shape_annotations_round_trip_and_validate()
    {
        var api = await AuthedClientAsync();
        var (docId, versionId) = await SeedConfirmedDocumentAsync(api);
        var url = $"/api/documents/{docId}/versions/{versionId}/annotations";

        // A highlight box (kind 1) carries a width/height and needs no text.
        var hl = await PostJson(api, url, new { pageIndex = 0, kind = 1, positionX = 0.1, positionY = 0.2, width = 0.3, height = 0.05, text = "", color = "#FFEB3B" });
        Assert.Equal(1, hl.GetProperty("kind").GetInt32());
        Assert.Equal(0.3, hl.GetProperty("width").GetDouble(), 3);
        Assert.Equal(0.05, hl.GetProperty("height").GetDouble(), 3);

        // An arrow (kind 3) carries a signed end-offset.
        var arrow = await PostJson(api, url, new { pageIndex = 0, kind = 3, positionX = 0.6, positionY = 0.7, width = -0.2, height = 0.1, text = "", color = "#F44336" });
        Assert.Equal(3, arrow.GetProperty("kind").GetInt32());
        Assert.Equal(-0.2, arrow.GetProperty("width").GetDouble(), 3);

        Assert.Equal(2, (await GetJson(api, url)).GetProperty("annotations").GetArrayLength());

        // Validation: a shape with no extent → 400; an unknown kind → 400; an out-of-range extent → 400.
        await AssertBadRequest(api, url, new { pageIndex = 0, kind = 1, positionX = 0.2, positionY = 0.2, text = "", color = "#FFEB3B" }, "INVALID_ANNOTATION_EXTENT");
        await AssertBadRequest(api, url, new { pageIndex = 0, kind = 9, positionX = 0.2, positionY = 0.2, width = 0.2, height = 0.2, text = "", color = "#FFEB3B" }, "INVALID_ANNOTATION_KIND");
        await AssertBadRequest(api, url, new { pageIndex = 0, kind = 2, positionX = 0.2, positionY = 0.2, width = 1.5, height = 0.2, text = "", color = "#FFEB3B" }, "INVALID_ANNOTATION_EXTENT");
    }

    [Fact]
    public async Task Confirmed_version_advertises_an_annotations_link()
    {
        var api = await AuthedClientAsync();
        var (docId, versionId) = await SeedConfirmedDocumentAsync(api);
        var version = await GetJson(api, $"/api/documents/{docId}/versions/{versionId}");
        Assert.NotNull(Link(version, "annotations"));
    }

    [Fact]
    public async Task Reader_without_CanAnnotate_can_view_but_not_create_or_edit()
    {
        // Creator (full-rights auto-grant) makes a repo + confirmed document, and creates a note.
        var (adminClientId, adminSecret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(adminClientId, adminSecret));
        var repoId = (await PostJson(admin, "/api/repositories", new { name = $"Notes {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(admin, $"/api/documents/{repoId}/children", new { name = "note-target" })).GetProperty("id").GetGuid();
        var created = await PostJson(admin, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("body")))).EnsureSuccessStatusCode();
        }
        await PutJson(admin, $"/api/documents/{docId}/versions/{versionId}", new { });
        var annotationsUrl = $"/api/documents/{docId}/versions/{versionId}/annotations";
        var noteId = (await PostJson(admin, annotationsUrl, new { pageIndex = 0, positionX = 0.2, positionY = 0.2, text = "admin note", color = "#FFEB3B" })).GetProperty("id").GetGuid();

        // A reader service account granted only CanSee + CanReadContent (NOT CanAnnotate) on the repo root.
        var (readerClientId, readerSecret) = await _factory.SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: false);
        var reader = _factory.CreateAuthedClient(await _factory.GetTokenAsync(readerClientId, readerSecret));
        var readerSaId = (await GetJson(reader, "/api/diagnostics/whoami")).GetProperty("serviceAccountId").GetGuid();
        await PutJson(admin, $"/api/documents/{repoId}/acl-entries/service-accounts/{readerSaId}", new { canSee = true, canReadContent = true });

        // The reader can view the notes (list shows canCreate = false)…
        var list = await GetJson(reader, annotationsUrl);
        Assert.Equal(1, list.GetProperty("annotations").GetArrayLength());
        Assert.False(list.GetProperty("canCreate").GetBoolean());

        // …but cannot create a note…
        var post = await reader.PostAsJsonAsync(annotationsUrl, new { pageIndex = 0, positionX = 0.3, positionY = 0.3, text = "reader note", color = "#8BC34A" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        // …nor edit an existing one (a HEAD-fetched etag isn't even needed — the right check comes first).
        var edit = await PutWithIfMatch(reader, $"{annotationsUrl}/{noteId}", Guid.NewGuid().ToString(), new { pageIndex = 0, positionX = 0.3, positionY = 0.3, text = "x", color = "#8BC34A" });
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        return _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
    }

    private static async Task<(Guid DocId, Guid VersionId)> SeedConfirmedDocumentAsync(HttpClient api)
    {
        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Notes {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "note-target" })).GetProperty("id").GetGuid();
        var created = await PostJson(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();

        using var storage = new HttpClient();
        (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("body")))).EnsureSuccessStatusCode();
        await PutJson(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        return (docId, versionId);
    }

    private static Task<HttpResponseMessage> PutWithIfMatch(HttpClient api, string url, string etag, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        return api.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteWithIfMatch(HttpClient api, string url, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        return api.SendAsync(request);
    }

    private static async Task AssertBadRequest(HttpClient api, string url, object body, string expectedErrorCode)
    {
        var response = await api.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedErrorCode, problem.GetProperty("errorCode").GetString());
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body) => await ReadJson(await client.PostAsJsonAsync(url, body));

    private static async Task<JsonElement> PutJson(HttpClient client, string url, object body) => await ReadJson(await client.PutAsJsonAsync(url, body));

    private static async Task<JsonElement> GetJson(HttpClient client, string url) => await ReadJson(await client.GetAsync(url));

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static string? Link(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links))
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.GetProperty("rel").GetString() == rel)
            {
                return link.GetProperty("href").GetString();
            }
        }

        return null;
    }
}
