using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage, exercising the WebDAV gateway (ADR "WebDAV
// gateway"): a user generates an app-specific WebDAV password, then a WebDAV client round-trips OPTIONS /
// PROPFIND / MKCOL / PUT / GET / MOVE / DELETE against the mounted archive. Wrong credentials are refused.
[Collection(E2ECollection.Name)]
public class DavGatewayTests
{
    // The personal space is named after its owner (ADR 0671), so its WebDAV/IMAP path segment is
    // whatever this test seeded as the display name — not the constant "Personal" it used to be.
    private const string Personal = "Dav User";

    private readonly E2EApiFactory _factory;

    public DavGatewayTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task WebDav_round_trips_browse_create_read_move_and_delete()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"Dav{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        // A user who can see the repository (tenant admin → CanSee via the IsTenantAdmin bypass).
        var email = $"dav-{Guid.NewGuid():N}@e2e.local";
        const string password = "dav-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Generate the app-specific WebDAV password.
        var gen = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var davPassword = gen.GetProperty("password").GetString()!;
        Assert.Equal(email, gen.GetProperty("username").GetString());

        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        async Task<HttpResponseMessage> DavAsync(string method, string path, byte[]? body = null, (string, string)[]? headers = null)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = basic } };
            if (body is not null) req.Content = new ByteArrayContent(body);
            foreach (var (k, v) in headers ?? []) req.Headers.TryAddWithoutValidation(k, v);
            return await dav.SendAsync(req);
        }

        // OPTIONS advertises DAV capabilities.
        var options = await DavAsync("OPTIONS", "/webdav");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        Assert.Contains("1", options.Headers.GetValues("DAV").First());

        // PROPFIND the root lists the repository as a collection.
        var rootList = await DavAsync("PROPFIND", "/webdav", headers: [("Depth", "1")]);
        Assert.Equal((HttpStatusCode)207, rootList.StatusCode);
        Assert.Contains(repoName, await rootList.Content.ReadAsStringAsync());

        // MKCOL a folder, PUT a file into it, GET it back byte-for-byte.
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("MKCOL", $"/webdav/{repoName}/wd")).StatusCode);
        var content = Encoding.UTF8.GetBytes("hello webdav");
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/wd/hello.txt", content)).StatusCode);

        // The MKCOL folder gets the Folder mask; the PUT file auto-classifies to Basic Entry — same as the API
        // (ADR "Folder mask on folders" / auto-classification at finalize).
        var children = (await TestJson.Get(owner, $"/api/documents/{repoId}/children")).GetProperty("children").EnumerateArray().ToList();
        Assert.Equal("Folder", children.Single(c => c.GetProperty("name").GetString() == "wd").GetProperty("documentType").GetString());
        var wdChildren = (await TestJson.Get(owner, $"/api/documents/{children.Single(c => c.GetProperty("name").GetString() == "wd").GetProperty("id").GetGuid()}/children")).GetProperty("children").EnumerateArray().ToList();
        Assert.Equal("Basic Entry", wdChildren.Single(c => c.GetProperty("name").GetString() == "hello").GetProperty("documentType").GetString());

        var get = await DavAsync("GET", $"/webdav/{repoName}/wd/hello.txt");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("hello webdav", await get.Content.ReadAsStringAsync());

        // PROPFIND the folder lists the file, and advertises write-lock capability per resource so lock-checking
        // editors (LibreOffice / Office) open files read/write rather than read-only (issue: WebDAV read/write).
        var folderXml = await (await DavAsync("PROPFIND", $"/webdav/{repoName}/wd", headers: [("Depth", "1")])).Content.ReadAsStringAsync();
        Assert.Contains("hello.txt", folderXml);
        Assert.Contains("<D:supportedlock>", folderXml);
        Assert.Contains("<D:write/>", folderXml);

        // MOVE (rename) then DELETE.
        var move = await DavAsync("MOVE", $"/webdav/{repoName}/wd/hello.txt", headers: [("Destination", $"/webdav/{repoName}/wd/renamed.txt")]);
        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DavAsync("GET", $"/webdav/{repoName}/wd/renamed.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DavAsync("DELETE", $"/webdav/{repoName}/wd/renamed.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DavAsync("GET", $"/webdav/{repoName}/wd/renamed.txt")).StatusCode);
    }

    [Theory]
    [InlineData(".crdownload")] // Chromium (Chrome/Edge/Brave/Opera/Vivaldi)
    [InlineData(".part")]       // Firefox
    [InlineData(".partial")]    // Internet Explorer / legacy EdgeHTML
    [InlineData(".dltemp")]     // legacy Opera (Presto)
    public async Task WebDav_download_temp_stages_then_commits_the_real_file_on_rename(string tempExt)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"Dav{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"dav-{Guid.NewGuid():N}@e2e.local";
        const string password = "dav-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        async Task<HttpResponseMessage> DavAsync(string method, string path, byte[]? body = null, (string, string)[]? headers = null)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = basic } };
            if (body is not null) req.Content = new ByteArrayContent(body);
            foreach (var (k, v) in headers ?? []) req.Headers.TryAddWithoutValidation(k, v);
            return await dav.SendAsync(req);
        }

        Assert.Equal(HttpStatusCode.Created, (await DavAsync("MKCOL", $"/webdav/{repoName}/dl")).StatusCode);
        var dlId = (await TestJson.Get(owner, $"/api/documents/{repoId}/children")).GetProperty("children")
            .EnumerateArray().Single(c => c.GetProperty("name").GetString() == "dl").GetProperty("id").GetGuid();

        // The browser's zero-byte placeholder at the real name creates the document EMPTY, and that is a
        // deliberate reversal (#762). It used to be discarded, on the reasoning that an empty document is
        // clutter — but discarding it means the file the OS just created answers 404 to the next read, and for
        // macOS's atomic save that is fatal: the editor writes its content to a scratch collection, finds no
        // original to swap over, and abandons without ever issuing the MOVE. A created-but-unwritten file is an
        // empty file on any filesystem; the honest representation is an empty document.
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/dl/report.txt", [])).StatusCode);
        // The bytes stream into a sibling .crdownload — staged, still NO document; the real name doesn't exist yet.
        var payload = Encoding.UTF8.GetBytes("the real downloaded content");
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/dl/report.txt{tempExt}", payload)).StatusCode);
        // The placeholder is READABLE while the download is in flight — empty, but there. Answering 404 to a
        // name we returned 201 for is the defect this whole change is about.
        var placeholder = await DavAsync("GET", $"/webdav/{repoName}/dl/report.txt");
        Assert.Equal(HttpStatusCode.OK, placeholder.StatusCode);
        Assert.Empty(await placeholder.Content.ReadAsByteArrayAsync());

        // …and it is ONE document, not one per step.
        Assert.Single((await TestJson.Get(owner, $"/api/documents/{dlId}/children")).GetProperty("children").EnumerateArray());

        // Download completes: the .crdownload is renamed to the final name → the real document is materialized.
        var move = await DavAsync("MOVE", $"/webdav/{repoName}/dl/report.txt{tempExt}",
            headers: [("Destination", $"/webdav/{repoName}/dl/report.txt")]);
        Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        var get = await DavAsync("GET", $"/webdav/{repoName}/dl/report.txt");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("the real downloaded content", await get.Content.ReadAsStringAsync());
        Assert.Equal("report", (await TestJson.Get(owner, $"/api/documents/{dlId}/children")).GetProperty("children")
            .EnumerateArray().Single().GetProperty("name").GetString());

        // A cancelled download deletes its .crdownload → the staged blob is dropped, still no document.
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/dl/other.txt{tempExt}", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DavAsync("DELETE", $"/webdav/{repoName}/dl/other.txt{tempExt}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DavAsync("GET", $"/webdav/{repoName}/dl/other.txt")).StatusCode);
    }

    [Fact]
    public async Task WebDav_clutter_is_filtered_from_the_repo_but_transient_files_stage_in_the_intray()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"Dav{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"dav-{Guid.NewGuid():N}@e2e.local";
        const string password = "dav-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        async Task<HttpResponseMessage> DavAsync(string method, string path, byte[]? body = null, (string, string)[]? headers = null)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = basic } };
            if (body is not null) req.Content = new ByteArrayContent(body);
            foreach (var (k, v) in headers ?? []) req.Headers.TryAddWithoutValidation(k, v);
            return await dav.SendAsync(req);
        }

        var bytes = Encoding.UTF8.GetBytes("x");

        // Into the repository: OS junk + transient/partial files are accepted (so Finder/Explorer's copy doesn't
        // error) but NOT filed as documents; a real file goes through normally.
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/._ghost.txt", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/.DS_Store", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/Thumbs.db", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/download.crdownload", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("MKCOL", $"/webdav/{repoName}/.Trashes")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{repoName}/real.txt", bytes)).StatusCode);

        var repoChildren = (await TestJson.Get(owner, $"/api/documents/{repoId}/children"))
            .GetProperty("children").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("real", repoChildren);            // the real file was filed (Name is the stem, ADR 0277)
        Assert.DoesNotContain("._ghost", repoChildren);
        Assert.DoesNotContain(".DS_Store", repoChildren);
        Assert.DoesNotContain("Thumbs.db", repoChildren);
        Assert.DoesNotContain("download", repoChildren);  // .crdownload is not filed in the permanent repository
        Assert.DoesNotContain(".Trashes", repoChildren);  // the junk directory was not created
        Assert.Single(repoChildren);

        // Into the Intray: OS junk is still discarded, but a transient/partial file legitimately stages.
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{Personal}/Intray/.DS_Store", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{Personal}/Intray/partial.crdownload", bytes)).StatusCode);

        var intrayList = await (await DavAsync("PROPFIND", $"/webdav/{Personal}/Intray", headers: [("Depth", "1")])).Content.ReadAsStringAsync();
        Assert.Contains("partial.crdownload", intrayList); // transient is allowed in the staging area
        Assert.DoesNotContain(".DS_Store", intrayList);    // OS junk is discarded even in the intray
    }

    [Fact]
    public async Task WebDav_intray_and_checkout_folders_work()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"Dav{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"dav-{Guid.NewGuid():N}@e2e.local";
        const string password = "dav-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        async Task<HttpResponseMessage> DavAsync(string method, string path, byte[]? body = null, (string, string)[]? headers = null)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = basic } };
            if (body is not null) req.Content = new ByteArrayContent(body);
            foreach (var (k, v) in headers ?? []) req.Headers.TryAddWithoutValidation(k, v);
            return await dav.SendAsync(req);
        }

        // The root lists the Personal folder (which nests Intray + Check-out) — not top-level Intray/Check-out.
        var rootXml = await (await DavAsync("PROPFIND", "/webdav", headers: [("Depth", "1")])).Content.ReadAsStringAsync();
        Assert.Contains(Personal, rootXml);

        // Personal lists the two virtual special folders alongside its real children.
        var personalXml = await (await DavAsync("PROPFIND", $"/webdav/{Personal}", headers: [("Depth", "1")])).Content.ReadAsStringAsync();
        Assert.Contains("Intray", personalXml);
        Assert.Contains("Check-out", personalXml);

        // Intray: PUT stages a raw object (no document), PROPFIND lists it, GET returns it, DELETE removes it.
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", $"/webdav/{Personal}/Intray/staged.txt", Encoding.UTF8.GetBytes("stage me"))).StatusCode);
        Assert.Contains("staged.txt", await (await DavAsync("PROPFIND", $"/webdav/{Personal}/Intray", headers: [("Depth", "1")])).Content.ReadAsStringAsync());
        Assert.Equal("stage me", await (await DavAsync("GET", $"/webdav/{Personal}/Intray/staged.txt")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await DavAsync("DELETE", $"/webdav/{Personal}/Intray/staged.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DavAsync("GET", $"/webdav/{Personal}/Intray/staged.txt")).StatusCode);

        // Check-out: create a document, check it out via the API, then browse/edit it in the WebDAV Check-out folder.
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "codoc" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("v1")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        (await api.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();

        Assert.Contains("codoc.txt", await (await DavAsync("PROPFIND", $"/webdav/{Personal}/Check-out", headers: [("Depth", "1")])).Content.ReadAsStringAsync());
        Assert.Equal("v1", await (await DavAsync("GET", $"/webdav/{Personal}/Check-out/codoc.txt")).Content.ReadAsStringAsync());

        // PUT saves an edited working copy to the stash; GET then returns the stash.
        Assert.Equal(HttpStatusCode.NoContent, (await DavAsync("PUT", $"/webdav/{Personal}/Check-out/codoc.txt", Encoding.UTF8.GetBytes("edited"))).StatusCode);
        Assert.Equal("edited", await (await DavAsync("GET", $"/webdav/{Personal}/Check-out/codoc.txt")).Content.ReadAsStringAsync());

        // LibreOffice's lock sidecar (.~lock.<file>#) must ROUND-TRIP — PUT then read it back — or the editor
        // reverts the document to read-only (ADR 0513). It stays HIDDEN from the folder listing, though.
        const string lockFile = $"/webdav/{Personal}/Check-out/.~lock.codoc.txt%23"; // %23 = '#'
        Assert.Equal(HttpStatusCode.Created, (await DavAsync("PUT", lockFile, Encoding.UTF8.GetBytes(",user,host,"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DavAsync("GET", lockFile)).StatusCode);
        Assert.Equal(HttpStatusCode.MultiStatus, (await DavAsync("PROPFIND", lockFile, headers: [("Depth", "0")])).StatusCode);
        // ...but it does NOT appear in the folder listing.
        Assert.DoesNotContain(".~lock", await (await DavAsync("PROPFIND", $"/webdav/{Personal}/Check-out", headers: [("Depth", "1")])).Content.ReadAsStringAsync());
        // And it deletes cleanly.
        Assert.Equal(HttpStatusCode.NoContent, (await DavAsync("DELETE", lockFile)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DavAsync("GET", lockFile)).StatusCode);
    }

    [Fact]
    public async Task Wrong_webdav_password_is_refused()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"dav-bad-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "dav-1234", "Dav User");
        using var dav = _factory.CreateClient();

        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/webdav");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:wrong")));
        req.Headers.TryAddWithoutValidation("Depth", "0");
        var response = await dav.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WebDav_hardening_range_copy_lock_and_quota()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repo = $"Hard{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repo });

        var email = $"hard-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "hard-1234", "Hard User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "hard-1234"));
        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        async Task<HttpResponseMessage> Dav(string method, string path, byte[]? body = null, params (string, string)[] headers)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = basic } };
            if (body is not null) req.Content = new ByteArrayContent(body);
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            return await dav.SendAsync(req);
        }

        var content = Encoding.UTF8.GetBytes("0123456789ABCDEF"); // 16 bytes
        Assert.Equal(HttpStatusCode.Created, (await Dav("PUT", $"/webdav/{repo}/data.txt", content)).StatusCode);

        // Range GET → 206 + the sliced bytes.
        var range = await Dav("GET", $"/webdav/{repo}/data.txt", null, ("Range", "bytes=4-7"));
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("4567", await range.Content.ReadAsStringAsync());
        Assert.Equal("bytes 4-7/16", range.Content.Headers.ContentRange!.ToString());

        // COPY → 201; the duplicate has the same bytes.
        Assert.Equal(HttpStatusCode.Created, (await Dav("COPY", $"/webdav/{repo}/data.txt", null, ("Destination", $"/webdav/{repo}/copy.txt"))).StatusCode);
        Assert.Equal("0123456789ABCDEF", await (await Dav("GET", $"/webdav/{repo}/copy.txt")).Content.ReadAsStringAsync());

        // LOCK → an opaque token; UNLOCK with a wrong token → 409, with the real token → 204.
        var lockResp = await Dav("LOCK", $"/webdav/{repo}/data.txt");
        Assert.Equal(HttpStatusCode.OK, lockResp.StatusCode);
        var token = lockResp.Headers.GetValues("Lock-Token").First().Trim('<', '>');
        Assert.StartsWith("opaquelocktoken:", token);
        Assert.Equal(HttpStatusCode.Conflict, (await Dav("UNLOCK", $"/webdav/{repo}/data.txt", null, ("Lock-Token", "<opaquelocktoken:00000000-0000-0000-0000-000000000000>"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Dav("UNLOCK", $"/webdav/{repo}/data.txt", null, ("Lock-Token", $"<{token}>"))).StatusCode);

        // Quota → 507: a tiny quota makes any further PUT exceed it.
        await _factory.SetTenantStorageQuotaAsync(tenantId, 1);
        Assert.Equal(HttpStatusCode.InsufficientStorage, (await Dav("PUT", $"/webdav/{repo}/toobig.txt", content)).StatusCode);
    }

    [Fact]
    public async Task Root_listing_is_acl_filtered()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var secretRepo = $"Secret{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = secretRepo }); // owned by the service account only

        // A plain user (no grant on the repo, not a tenant admin) with a WebDAV password.
        var email = $"plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "plain-1234", "Plain User");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "plain-1234"));
        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/webdav") { Headers = { Authorization = basic } };
        req.Headers.TryAddWithoutValidation("Depth", "1");
        var xml = await (await dav.SendAsync(req)).Content.ReadAsStringAsync();

        Assert.Contains("Plain User", xml);         // the user's own personal space, named after them (ADR 0671)
        Assert.DoesNotContain(secretRepo, xml);      // a shared repo they can't see is hidden
    }
}
