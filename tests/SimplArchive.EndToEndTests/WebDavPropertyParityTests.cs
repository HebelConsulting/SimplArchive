using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SimplArchive.EndToEndTests;

// The Intray and Check-out folders are drawn by the same OS client as the repository tree, so they have to
// ANSWER the same way. They do not: the tree composes its properties in `WebDavMiddleware.PropFor`, the special
// areas in `CollectionProp`/`FileProp`, and the two drifted apart without anything noticing — every existing
// WebDAV test asserts STATUS CODES, which were identical the whole time (#794).
//
// Guarded as a PARITY between the two property sets rather than as "the Intray reports X". The defect is drift
// between two answers to one question, and a test pinning one of them lets the other move again — the same
// reasoning as `DesktopFilingRootsParityTests` (ADR 0689).
[Collection(E2ECollection.Name)]
public class WebDavPropertyParityTests
{
    private const string Personal = "Prop User";

    private static readonly XNamespace Dav = "DAV:";

    private readonly E2EApiFactory _factory;

    public WebDavPropertyParityTests(E2EApiFactory factory) => _factory = factory;

    /// <summary>A special folder answers PROPFIND with the same properties a tree folder does.</summary>
    [Fact]
    public async Task A_special_folder_reports_the_same_properties_as_a_tree_folder()
    {
        var ctx = await SetupAsync();

        var tree = await PropertyNamesAsync(ctx, $"/SimplArchive/{ctx.RepoName}");
        var intray = await PropertyNamesAsync(ctx, $"/SimplArchive/{Personal}/Intray");

        Assert.Equal(tree, intray);
    }

    /// <summary>…and so does an item inside one, compared with a document in the tree.</summary>
    [Fact]
    public async Task An_intray_item_reports_the_same_properties_as_a_tree_document()
    {
        var ctx = await SetupAsync();
        var docName = await SeedDocumentAsync(ctx);
        var item = $"item{Guid.NewGuid().ToString("N")[..8]}.docx";
        (await DavAsync(ctx, "PUT", $"/SimplArchive/{Personal}/Intray/{item}", Encoding.UTF8.GetBytes("staged")))
            .EnsureSuccessStatusCode();

        var tree = await PropertyNamesAsync(ctx, $"/SimplArchive/{ctx.RepoName}/{docName}");
        var staged = await PropertyNamesAsync(ctx, $"/SimplArchive/{Personal}/Intray/{item}");

        Assert.Equal(tree, staged);
    }

    /// <summary>A special folder's modified time is real, and moves when something is written into it.</summary>
    /// <remarks>
    /// `CollectionProp` hardcoded <c>getlastmodified</c> to the UNIX EPOCH, so the Intray claimed it had not
    /// changed since 1970 — while the items inside it carried today's date. A client that revalidates a cached
    /// listing against the collection's mtime is being told, correctly formatted and permanently wrong, that
    /// there is nothing new to fetch.
    /// </remarks>
    [Fact]
    public async Task A_special_folders_modified_time_is_real_and_advances_on_a_write()
    {
        var ctx = await SetupAsync();
        var intray = $"/SimplArchive/{Personal}/Intray";

        var before = await LastModifiedAsync(ctx, intray);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, before);

        // A write into the folder is a change to the folder. Second granularity is all `getlastmodified` carries
        // (RFC 1123), so the write has to land in a later second than the reading of `before`.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        (await DavAsync(ctx, "PUT", $"{intray}/fresh{Guid.NewGuid().ToString("N")[..8]}.txt", Encoding.UTF8.GetBytes("new")))
            .EnsureSuccessStatusCode();

        Assert.True(await LastModifiedAsync(ctx, intray) > before, "the Intray's modified time did not advance after a write into it");
    }

    /// <summary>A file answers with an ETag, and the listing agrees with the download.</summary>
    /// <remarks>
    /// Measured (#794). Immediately after writing a document, a word processor asks for exactly two properties —
    /// <c>getlastmodified</c> and <c>getetag</c> — to confirm the write landed. This gateway emitted no ETag
    /// anywhere, so it answered <c>207</c> with a propstat of <c>200 OK</c> that simply did not mention the
    /// property. The status line said yes and the body said nothing; the editor could not confirm its own write
    /// and retried the whole save four times in six seconds before rolling back over its own good file.
    ///
    /// Asserted as an AGREEMENT between the property and the header, because a client may validate with either
    /// and the two disagreeing is worse than neither existing.
    /// </remarks>
    [Fact]
    public async Task A_file_has_an_etag_and_the_listing_agrees_with_the_download()
    {
        var ctx = await SetupAsync();
        var item = $"etag{Guid.NewGuid().ToString("N")[..8]}.docx";
        var path = $"/SimplArchive/{Personal}/Intray/{item}";
        (await DavAsync(ctx, "PUT", path, Encoding.UTF8.GetBytes("staged content"))).EnsureSuccessStatusCode();

        var listed = await PropertyValueAsync(ctx, path, "getetag");
        Assert.False(string.IsNullOrWhiteSpace(listed), "the file reported no getetag");

        var downloaded = (await DavAsync(ctx, "GET", path)).Headers.ETag?.ToString();
        Assert.Equal(listed, downloaded);
    }

    /// <summary>The ETag changes when the bytes do — otherwise it cannot confirm anything.</summary>
    [Fact]
    public async Task An_etag_changes_when_the_content_changes()
    {
        var ctx = await SetupAsync();
        var item = $"etag{Guid.NewGuid().ToString("N")[..8]}.docx";
        var path = $"/SimplArchive/{Personal}/Intray/{item}";

        (await DavAsync(ctx, "PUT", path, Encoding.UTF8.GetBytes("first"))).EnsureSuccessStatusCode();
        var before = await PropertyValueAsync(ctx, path, "getetag");

        (await DavAsync(ctx, "PUT", path, Encoding.UTF8.GetBytes("a materially longer second version"))).EnsureSuccessStatusCode();

        Assert.NotEqual(before, await PropertyValueAsync(ctx, path, "getetag"));
    }

    /// <summary>A tree document answers the same way — one contract, not one per area.</summary>
    [Fact]
    public async Task A_tree_document_reports_an_etag_too()
    {
        var ctx = await SetupAsync();
        var docName = await SeedDocumentAsync(ctx);
        var path = $"/SimplArchive/{ctx.RepoName}/{docName}";

        var listed = await PropertyValueAsync(ctx, path, "getetag");
        Assert.False(string.IsNullOrWhiteSpace(listed), "the tree document reported no getetag");
        Assert.Equal(listed, (await DavAsync(ctx, "GET", path)).Headers.ETag?.ToString());
    }

    private async Task<string?> PropertyValueAsync(Context ctx, string path, string property)
    {
        var response = await DavAsync(ctx, "PROPFIND", path, null, ("Depth", "0"));
        Assert.True(response.IsSuccessStatusCode, $"PROPFIND {path} returned {(int)response.StatusCode}");
        return XDocument.Parse(await response.Content.ReadAsStringAsync())
            .Descendants(Dav + property).FirstOrDefault()?.Value;
    }

    private async Task<List<string>> PropertyNamesAsync(Context ctx, string path)
    {
        var response = await DavAsync(ctx, "PROPFIND", path, null, ("Depth", "0"));
        Assert.True(response.IsSuccessStatusCode, $"PROPFIND {path} returned {(int)response.StatusCode}");
        var prop = XDocument.Parse(await response.Content.ReadAsStringAsync())
            .Descendants(Dav + "prop").First();

        // The direct children only — `resourcetype` and `supportedlock` have children of their own, and those
        // are part of the VALUE, not of the property set being compared.
        return [.. prop.Elements().Select(e => e.Name.LocalName).Order(StringComparer.Ordinal)];
    }

    private async Task<DateTimeOffset> LastModifiedAsync(Context ctx, string path)
    {
        var response = await DavAsync(ctx, "PROPFIND", path, null, ("Depth", "0"));
        Assert.True(response.IsSuccessStatusCode, $"PROPFIND {path} returned {(int)response.StatusCode}");
        var raw = XDocument.Parse(await response.Content.ReadAsStringAsync())
            .Descendants(Dav + "getlastmodified").First().Value;
        return DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record Context(HttpClient Owner, Guid RepoId, string RepoName, AuthenticationHeaderValue Basic, HttpClient Dav);

    private async Task<Context> SetupAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Prop{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"prop-{Guid.NewGuid():N}@e2e.local";
        const string password = "prop-1234";
        await _factory.SeedUserAsync(tenantId, email, password, Personal);
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return new Context(owner, repoId, repoName, basic, _factory.CreateClient());
    }

    private async Task<string> SeedDocumentAsync(Context ctx)
    {
        var stem = $"doc{Guid.NewGuid().ToString("N")[..8]}";
        var docId = (await TestJson.Post(ctx.Owner, $"/api/documents/{ctx.RepoId}/children", new { name = stem })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(ctx.Owner, $"/api/documents/{docId}/versions", new { fileExtension = ".docx" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("original")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(ctx.Owner, $"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });
        return stem + ".docx";
    }

    private Task<HttpResponseMessage> DavAsync(Context ctx, string method, string path, byte[]? body = null, params (string Key, string Value)[] headers)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = ctx.Basic } };
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
}
