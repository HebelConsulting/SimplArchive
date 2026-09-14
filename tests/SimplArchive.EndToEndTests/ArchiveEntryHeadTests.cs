using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// The archive-entry routes had NO test coverage at all, which is how the missing HEAD companion (#1173) got
// there: the listing route has one, the content route did not, and nothing said either way.
//
// These cover the companion and, incidentally, the GET beside it — the first coverage this controller has had.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ArchiveEntryHeadTests
{
    private readonly E2EApiFactory _factory;

    public ArchiveEntryHeadTests(E2EApiFactory factory) => _factory = factory;

    private const string EntryPath = "inside/hello.txt";
    private const string EntryText = "hello from inside the zip";

    private static byte[] Zip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(EntryPath).Open());
            writer.Write(EntryText);
        }

        return buffer.ToArray();
    }

    private async Task<(HttpClient Api, Guid DocumentId)> ZipDocumentAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"Zip{Guid.NewGuid():N}"[..10];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"zip-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "zip-1234", "Zip User");
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "zip-1234"));

        // Filed over WebDAV — the shortest real upload path, and it goes through the same finalizer.
        var dav = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var basic = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{dav.GetProperty("password").GetString()}")));
        var name = $"bundle-{Guid.NewGuid():N}"[..14];
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/{repoName}/{name}.zip")
        {
            Content = new ByteArrayContent(Zip()),
            Headers = { Authorization = basic },
        })
        {
            using var client = _factory.CreateClient();
            (await client.SendAsync(put)).EnsureSuccessStatusCode();
        }

        var children = await TestJson.Get(api, $"/api/documents/{repoId}/children");
        var documentId = children.GetProperty("children").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

        return (api, documentId);
    }

    [Fact]
    public async Task The_entry_route_answers_HEAD_with_the_size_and_no_body()
    {
        var (api, documentId) = await ZipDocumentAsync();
        using var _a = api;

        var url = $"/api/documents/{documentId}/archive-entries/content?path={Uri.EscapeDataString(EntryPath)}";

        // The GET first, so the HEAD is compared against a route that genuinely serves this entry.
        var get = await api.GetAsync(url);
        get.EnsureSuccessStatusCode();
        Assert.Equal(EntryText, await get.Content.ReadAsStringAsync());

        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await api.SendAsync(head);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        // The size is what a HEAD is asked for. Answering from the zip's presence alone would report success
        // for a path the GET then 404s, so the companion reads the entry exactly as the GET does.
        Assert.Equal(EntryText.Length, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task A_path_that_is_not_in_the_archive_is_not_found_by_HEAD_either()
    {
        var (api, documentId) = await ZipDocumentAsync();
        using var _a = api;

        using var head = new HttpRequestMessage(
            HttpMethod.Head,
            $"/api/documents/{documentId}/archive-entries/content?path={Uri.EscapeDataString("inside/absent.txt")}");

        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(head)).StatusCode);
    }
}
