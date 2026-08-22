using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// Editing a document IN PLACE — in the repository tree, not in the Check-out folder — over WebDAV (ADR 0562).
//
// ADR 0508 already made the save-by-rename dance work inside the special folders. In the tree the temporary write
// was discarded by the clutter filter, so the committing rename had no source and the save failed: the reason
// editing worked only if you checked the document out first. It now performs an IMPLICIT check-out instead.
//
// The assertions are mostly about what must NOT happen: no new version appears (a save is not a publish), no
// second document is created beside the first, and someone else's document is not quietly writable.
[Collection(E2ECollection.Name)]
public class WebDavImplicitCheckoutTests
{
    // The personal space is named after its owner (ADR 0671), so its WebDAV/IMAP path segment is
    // whatever this test seeded as the display name — not the constant "Personal" it used to be.
    private const string Personal = "Impl User";

    private const string EditorAgent = "TestOfficeSuite/1.0 (save-by-rename)";

    private readonly E2EApiFactory _factory;

    public WebDavImplicitCheckoutTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Saving_in_place_checks_the_document_out_and_stashes_the_edit()
    {
        var ctx = await SetupAsync();
        var (docId, docName) = await SeedDocumentAsync(ctx, ".docx", "original");

        var edited = Encoding.UTF8.GetBytes("edited in place");
        await SaveByRenameAsync(ctx, docName, edited);

        // 1. The document is checked out to the editing user, and says what did it. Nobody pressed "check out",
        //    so the agent is the only thing that can explain the state to them.
        var checkouts = await TestJson.Get(ctx.Api, "/api/checkouts");
        var row = checkouts.GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == docId);
        Assert.Equal(EditorAgent, row.GetProperty("implicitAgent").GetString());
        Assert.True(row.GetProperty("isModified").GetBoolean());

        // 2. The edit is the working copy, readable from the Check-out folder — the same stash an explicit
        //    check-out uses, which is what makes check-in, discard and the idle sweep work unchanged.
        var stash = await DavAsync(ctx, "GET", $"/webdav/{Personal}/Check-out/{docName}");
        Assert.Equal(HttpStatusCode.OK, stash.StatusCode);
        Assert.Equal(edited, await stash.Content.ReadAsByteArrayAsync());

        // 3. Saving is NOT publishing: still one confirmed version, still the original bytes for everyone else.
        //    This is the assertion that would fail if the rename were ever turned into a silent new version.
        var versions = await TestJson.Get(ctx.Api, $"/api/documents/{docId}/versions");
        Assert.Single(versions.GetProperty("versions").EnumerateArray(),
            v => v.GetProperty("status").GetString() == "Confirmed");

        // 4. And no second document appeared beside it from the temporary file.
        var children = await TestJson.Get(ctx.Api, $"/api/documents/{ctx.RepoId}/children");
        Assert.Single(children.GetProperty("children").EnumerateArray());
    }

    [Fact]
    public async Task A_second_save_reuses_the_same_check_out_rather_than_taking_another()
    {
        var ctx = await SetupAsync();
        var (docId, docName) = await SeedDocumentAsync(ctx, ".docx", "original");

        await SaveByRenameAsync(ctx, docName, Encoding.UTF8.GetBytes("first"));
        var first = await CheckedOutAtAsync(ctx, docId);

        await SaveByRenameAsync(ctx, docName, Encoding.UTF8.GetBytes("second"));

        // The lock is not re-taken (which would restart the idle clock on every keystroke-triggered autosave) and
        // the stash simply carries the newer bytes.
        Assert.Equal(first, await CheckedOutAtAsync(ctx, docId));
        var stash = await DavAsync(ctx, "GET", $"/webdav/{Personal}/Check-out/{docName}");
        Assert.Equal("second", await stash.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Someone_elses_check_out_refuses_the_save_instead_of_swallowing_it()
    {
        var ctx = await SetupAsync();
        var (docId, docName) = await SeedDocumentAsync(ctx, ".docx", "original");

        // A second user in the same tenant holds the lock.
        var otherEmail = $"other-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(ctx.TenantId, otherEmail, "other-1234", "Other User");
        await _factory.GrantTenantAdminAsync(otherEmail);
        using var otherApi = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(otherEmail, "other-1234"));
        (await otherApi.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();

        // 423 Locked, not a silent success: an editor that believes it saved and did not is the worst outcome
        // available here, because the user closes the file and the work is gone.
        var response = await CommitRenameAsync(ctx, docName, Encoding.UTF8.GetBytes("should not land"));
        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);

        // And the other user's stash is untouched by the attempt.
        var checkouts = await TestJson.Get(otherApi, "/api/checkouts");
        Assert.False(checkouts.GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == docId).GetProperty("isModified").GetBoolean());
    }

    [Fact]
    public async Task An_explicit_check_out_is_not_labelled_as_automatic()
    {
        var ctx = await SetupAsync();
        var (docId, _) = await SeedDocumentAsync(ctx, ".docx", "original");
        (await ctx.Api.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();

        // The marker means "you did not ask for this". A check-out the user pressed the button for must not carry
        // it, or the label stops meaning anything and both clients show it on every row.
        var checkouts = await TestJson.Get(ctx.Api, "/api/checkouts");
        var row = checkouts.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == docId);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("implicitAgent").ValueKind);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private sealed record Context(HttpClient Owner, HttpClient Api, Guid TenantId, Guid RepoId, string RepoName, AuthenticationHeaderValue Basic, HttpClient Dav);

    private async Task<Context> SetupAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Dav{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"impl-{Guid.NewGuid():N}@e2e.local";
        const string password = "impl-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Impl User");
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return new Context(owner, api, tenantId, repoId, repoName, basic, _factory.CreateClient());
    }

    private async Task<(Guid Id, string FileName)> SeedDocumentAsync(Context ctx, string ext, string content)
    {
        var stem = $"doc{Guid.NewGuid().ToString("N")[..8]}";
        var docId = (await TestJson.Post(ctx.Owner, $"/api/documents/{ctx.RepoId}/children", new { name = stem })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(ctx.Owner, $"/api/documents/{docId}/versions", new { fileExtension = ext });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(ctx.Owner, $"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });
        return (docId, stem + ext);
    }

    private Task<HttpResponseMessage> DavAsync(Context ctx, string method, string path, byte[]? body = null, params (string Key, string Value)[] headers)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = ctx.Basic } };
        request.Headers.TryAddWithoutValidation("User-Agent", EditorAgent);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return ctx.Dav.SendAsync(request);
    }

    // The save-by-rename sequence, against a document where it LIVES rather than in a special folder: owner
    // sidecar, temporary file, delete the original, rename the temporary over it, drop the sidecar.
    private async Task SaveByRenameAsync(Context ctx, string docName, byte[] content)
    {
        var response = await CommitRenameAsync(ctx, docName, content);
        Assert.True(response.IsSuccessStatusCode, $"the committing rename returned {(int)response.StatusCode}");
    }

    private async Task<HttpResponseMessage> CommitRenameAsync(Context ctx, string docName, byte[] content)
    {
        var basePath = $"/webdav/{ctx.RepoName}";
        var temp = $"{Guid.NewGuid().ToString("N")[..8]}.tmp";

        (await DavAsync(ctx, "PUT", $"{basePath}/~${docName}", Encoding.UTF8.GetBytes("owner"))).EnsureSuccessStatusCode();
        (await DavAsync(ctx, "PUT", $"{basePath}/{temp}", content)).EnsureSuccessStatusCode();
        return await DavAsync(ctx, "MOVE", $"{basePath}/{temp}", null, ("Destination", $"{basePath}/{docName}"));
    }

    private static async Task<DateTimeOffset> CheckedOutAtAsync(Context ctx, Guid documentId)
    {
        var checkouts = await TestJson.Get(ctx.Api, "/api/checkouts");
        return checkouts.GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == documentId)
            .GetProperty("checkedOutAt").GetDateTimeOffset();
    }
}
