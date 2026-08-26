using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// A document REFERENCED into a folder is another appearance of that document, so the mount must show it
// (#769). ADR 0509 binds this mount to the Repositories tree — "same nodes, same order, same subtrees, and
// nothing else" — and the tree shows references. Without this the same archive presented two shapes: the
// workbench listed a referenced invoice in the folder its owner filed it into, and the mounted drive listed
// that folder without it.
//
// The three behaviours the issue said to DECIDE rather than discover are asserted here, because each is a place
// where a plausible guess loses data or surprises the user.
[Collection(E2ECollection.Name)]
public class WebDavReferencedDocumentTests
{
    private readonly E2EApiFactory _factory;

    public WebDavReferencedDocumentTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_referenced_document_is_listed_readable_writable_and_unplaceable()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"Ref{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"davref-{Guid.NewGuid():N}@e2e.local";
        const string password = "davref-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav Ref User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        // A document in one folder, referenced into another — the ordinary case the issue names: an invoice
        // that belongs in both a project folder and a year folder.
        var homeName = $"home{Guid.NewGuid():N}"[..8];
        var homeId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = homeName })).GetProperty("id").GetGuid();
        var workingName = $"work{Guid.NewGuid():N}"[..8];
        var workingId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = workingName })).GetProperty("id").GetGuid();

        var docName = $"invoice{Guid.NewGuid():N}"[..10];
        var docId = (await TestJson.Post(api, $"/api/documents/{homeId}/children", new { name = docName })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes("original")))).EnsureSuccessStatusCode();
        }

        (await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        await TestJson.Post(api, $"/api/documents/{workingId}/references", new { targetId = docId });

        var path = $"/SimplArchive/{repoName}/{workingName}/{docName}.txt";

        // LISTED: the folder holding only a reference is not an empty folder on the drive.
        using (var propfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/SimplArchive/{repoName}/{workingName}"))
        {
            propfind.Headers.Authorization = basic;
            propfind.Headers.Add("Depth", "1");
            var body = await (await dav.SendAsync(propfind)).Content.ReadAsStringAsync();
            Assert.Contains($"{docName}.txt", body);
        }

        // READABLE: it resolves to the target's current version, not to a stub or a 404.
        using (var get = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Authorization = basic } })
        {
            var response = await dav.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("original", await response.Content.ReadAsStringAsync());
        }

        // WRITABLE, onto the TARGET: a reference IS the document, so saving through it versions the document —
        // the same result as editing it in its real location. Refusing was the alternative, and a refused save
        // is what makes a word processor delete the original (#764).
        using (var put = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("edited through the reference")),
            Headers = { Authorization = basic },
        })
        {
            var response = await dav.SendAsync(put);
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.OK,
                $"PUT through the reference answered {response.StatusCode}");
        }

        var versions = await TestJson.Get(api, $"/api/documents/{docId}/versions");
        Assert.True(versions.GetProperty("versions").GetArrayLength() >= 2, "the write should have versioned the TARGET");

        // UNPLACEABLE, and this is the one that loses data if guessed wrong: DELETE removes the appearance,
        // never the document. A user tidying a working folder on a mounted drive must not destroy something
        // still filed where they were not looking.
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, path) { Headers = { Authorization = basic } })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await dav.SendAsync(delete)).StatusCode);
        }

        Assert.Empty((await TestJson.Get(api, $"/api/documents/{workingId}/references")).GetProperty("references").EnumerateArray());

        // The document itself is untouched, and still in its real home.
        var stillThere = await TestJson.Get(api, $"/api/documents/{docId}");
        Assert.Equal(docName, stillThere.GetProperty("name").GetString());
        using (var get = new HttpRequestMessage(HttpMethod.Get, $"/SimplArchive/{repoName}/{homeName}/{docName}.txt") { Headers = { Authorization = basic } })
        {
            Assert.Equal(HttpStatusCode.OK, (await dav.SendAsync(get)).StatusCode);
        }
    }

    // One wire name can only mean one thing. A referenced document whose name clashes with a real child of the
    // same folder is dropped in favour of the child, and the drop is logged — the alternative is two entries a
    // client cannot tell apart and a save-by-name landing on whichever the server happened to pick.
    [Fact]
    public async Task A_reference_whose_name_clashes_with_a_real_child_does_not_shadow_it()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"Clash{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"davclash-{Guid.NewGuid():N}@e2e.local";
        const string password = "davclash-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav Clash User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        // The same NAME in two folders: one is a real child of the working folder, the other is referenced
        // into it from elsewhere.
        var sharedName = $"clash{Guid.NewGuid():N}"[..10];
        var workingName = $"wk{Guid.NewGuid():N}"[..8];
        var workingId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = workingName })).GetProperty("id").GetGuid();
        var elsewhereName = $"el{Guid.NewGuid():N}"[..8];
        var elsewhereId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = elsewhereName })).GetProperty("id").GetGuid();

        var childId = await CreateDocumentAsync(api, workingId, sharedName, "the real child");
        var strangerId = await CreateDocumentAsync(api, elsewhereId, sharedName, "the referenced stranger");
        await TestJson.Post(api, $"/api/documents/{workingId}/references", new { targetId = strangerId });

        // The name resolves to the CHILD, so a client reading it gets the file it can see in the app.
        using var get = new HttpRequestMessage(HttpMethod.Get, $"/SimplArchive/{repoName}/{workingName}/{sharedName}.txt") { Headers = { Authorization = basic } };
        var response = await dav.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("the real child", await response.Content.ReadAsStringAsync());

        // …and the listing offers that name exactly once, rather than twice. Counted on the HREF specifically:
        // each entry also carries the name in its displayname, so a naive count of the bare string finds two
        // per entry and would call one entry two.
        using var propfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/SimplArchive/{repoName}/{workingName}");
        propfind.Headers.Authorization = basic;
        propfind.Headers.Add("Depth", "1");
        var body = await (await dav.SendAsync(propfind)).Content.ReadAsStringAsync();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, $"href>[^<]*{System.Text.RegularExpressions.Regex.Escape($"{sharedName}.txt")}<"));

        Assert.NotEqual(childId, strangerId);
    }

    private static async Task<Guid> CreateDocumentAsync(HttpClient api, Guid parentId, string name, string content)
    {
        var docId = (await TestJson.Post(api, $"/api/documents/{parentId}/children", new { name })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        (await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        return docId;
    }
}
