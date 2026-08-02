using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// Atomic-save over the WebDAV special folders (ADR 0508). Real office apps never overwrite in place — they write
// a sibling TEMP file then rename it over the target (Microsoft Office additionally DELETEs the original and both
// office suites create a lock/owner file; macOS Preview renames the original away to a backup; some PDF tools
// commit via COPY). This drives each editor's exact save sequence, for every office format, against BOTH the
// Check-out folder (edits must land in the document's stash) and the Inbox (edits must land in the staged object)
// — on real Postgres + object storage.
//
// Both test methods share a SINGLE tenant/user (created once) and give every round-trip unique file names — so the
// whole matrix adds just one per-tenant storage bucket, not one per case (which would exhaust the shared E2E
// server's bucket budget and 500 every later test).
[Collection(E2ECollection.Name)]
public class WebDavAtomicSaveTests
{
    private static readonly string[] OfficeFormats = [".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt", ".rtf", ".odt", ".ods", ".odp"];

    private readonly E2EApiFactory _factory;

    public WebDavAtomicSaveTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Office_suites_save_atomically_across_all_formats()
    {
        var ctx = await SetupAsync();
        foreach (var ext in OfficeFormats)
        {
            foreach (var suite in new[] { "MsOffice", "LibreOffice", "VsCode" })
            {
                await AssertSaveRoundTripAsync(ctx, ext, suite);
            }
        }
    }

    [Fact]
    public async Task App_specific_editors_save_atomically()
    {
        var ctx = await SetupAsync();
        await AssertSaveRoundTripAsync(ctx, ".pdf", "Preview");  // macOS Preview — backup dance (doc as move source)
        await AssertSaveRoundTripAsync(ctx, ".pdf", "Acrobat");  // Adobe Acrobat/Reader — COPY-commit
        await AssertSaveRoundTripAsync(ctx, ".ac2", "Banana");   // Banana Accounting — temp+rename
        await AssertSaveRoundTripAsync(ctx, ".img", "Chirp");    // CHIRP radio programmer — direct save
        await AssertSaveRoundTripAsync(ctx, ".csv", "Chirp");
    }

    // ---- shared per-test-method context (one tenant / user / webdav password / repo) ----------------------

    private sealed record Context(HttpClient Owner, HttpClient Api, Guid RepoId, AuthenticationHeaderValue Basic, HttpClient Dav);

    private async Task<Context> SetupAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Dav{Guid.NewGuid():N}"[..12] })).GetProperty("id").GetGuid();

        var email = $"dav-{Guid.NewGuid():N}@e2e.local";
        const string password = "dav-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav User");
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return new Context(owner, api, repoId, basic, _factory.CreateClient());
    }

    // One full round-trip for a (format, editor): a freshly checked-out document edited via the Check-out folder
    // (→ its stash) and a staged Inbox file edited via the Inbox (→ the staged object). Unique names per call.
    private async Task AssertSaveRoundTripAsync(Context ctx, string ext, string suite)
    {
        Task<HttpResponseMessage> Dav(string method, string path, byte[]? body = null, (string, string)[]? headers = null)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = ctx.Basic } };
            if (body is not null) req.Content = new ByteArrayContent(body);
            foreach (var (k, v) in headers ?? []) req.Headers.TryAddWithoutValidation(k, v);
            return ctx.Dav.SendAsync(req);
        }

        var id = Guid.NewGuid().ToString("N")[..10];

        // Check-out: a checked-out document, edited via WebDAV → its bytes must reach the stash.
        var docId = (await TestJson.Post(ctx.Owner, $"/api/documents/{ctx.RepoId}/children", new { name = $"co{id}" })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(ctx.Owner, $"/api/documents/{docId}/versions", new { fileExtension = ext });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("original")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(ctx.Owner, $"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });
        (await ctx.Api.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();

        var checkoutEdit = Encoding.UTF8.GetBytes($"checkout {suite} {ext} {id}");
        await SaveAtomicallyAsync(Dav, "Check-out", $"co{id}{ext}", checkoutEdit, suite);
        var checkoutBack = await Dav("GET", $"/webdav/Personal/Check-out/co{id}{ext}");
        Assert.Equal(HttpStatusCode.OK, checkoutBack.StatusCode);
        Assert.Equal(checkoutEdit, await checkoutBack.Content.ReadAsByteArrayAsync());

        // Inbox: a staged file, edited via WebDAV → the staged object must hold the edit.
        var inboxName = $"in{id}{ext}";
        (await Dav("PUT", $"/webdav/Personal/Inbox/{inboxName}", Encoding.UTF8.GetBytes("original"))).EnsureSuccessStatusCode();

        var inboxEdit = Encoding.UTF8.GetBytes($"inbox {suite} {ext} {id}");
        await SaveAtomicallyAsync(Dav, "Inbox", inboxName, inboxEdit, suite);
        var inboxBack = await Dav("GET", $"/webdav/Personal/Inbox/{inboxName}");
        Assert.Equal(HttpStatusCode.OK, inboxBack.StatusCode);
        Assert.Equal(inboxEdit, await inboxBack.Content.ReadAsByteArrayAsync());
    }

    // Emits one editor's atomic-save sequence saving `content` onto /webdav/Personal/{folder}/{name}, asserting
    // every WebDAV op succeeds (2xx). Temp names are unique per call.
    private static async Task SaveAtomicallyAsync(Func<string, string, byte[]?, (string, string)[]?, Task<HttpResponseMessage>> dav, string folder, string name, byte[] content, string suite)
    {
        var basePath = $"/webdav/Personal/{folder}";
        var target = $"{basePath}/{name}";
        var rand = Guid.NewGuid().ToString("N")[..8];

        async Task Ok(string method, string path, byte[]? body = null, (string, string)[]? headers = null)
        {
            var resp = await dav(method, path, body, headers);
            Assert.True(resp.IsSuccessStatusCode, $"{suite}: {method} {path} -> {(int)resp.StatusCode} {resp.StatusCode}");
        }

        switch (suite)
        {
            case "MsOffice":
                await Ok("PUT", $"{basePath}/~${name}", Encoding.UTF8.GetBytes("owner"));
                await Ok("PUT", $"{basePath}/{rand}.tmp", content);
                await Ok("DELETE", target);
                await Ok("MOVE", $"{basePath}/{rand}.tmp", headers: [("Destination", target)]);
                await Ok("DELETE", $"{basePath}/~${name}");
                break;

            case "LibreOffice":
                await Ok("PUT", $"{basePath}/.~lock.{name}#", Encoding.UTF8.GetBytes("lock"));
                var lockResp = await dav("LOCK", target, null, [("Timeout", "Second-600")]);
                Assert.True(lockResp.IsSuccessStatusCode, $"LibreOffice: LOCK {target} -> {(int)lockResp.StatusCode}");
                var lockToken = lockResp.Headers.TryGetValues("Lock-Token", out var t) ? t.First().Trim('<', '>') : "";
                await Ok("PUT", $"{basePath}/lu{rand}", content);
                await Ok("MOVE", $"{basePath}/lu{rand}", headers: [("Destination", target), ("If", $"(<{lockToken}>)")]);
                await Ok("UNLOCK", target, null, [("Lock-Token", $"<{lockToken}>")]);
                await Ok("DELETE", $"{basePath}/.~lock.{name}#");
                break;

            case "VsCode":
                await Ok("PUT", $"{basePath}/{name}.{rand}.tmp", content);
                await Ok("MOVE", $"{basePath}/{name}.{rand}.tmp", headers: [("Destination", target), ("Overwrite", "T")]);
                break;

            case "Preview":
                // macOS replaceItemAtURL "backup dance": write a temp, rename the ORIGINAL away to a backup, rename
                // the temp over the target, then delete the backup.
                var pvTemp = $"{basePath}/.{name}.sb-{rand}";
                var pvBackup = $"{basePath}/.{name}.bak-{rand}";
                await Ok("PUT", pvTemp, content);
                await Ok("MOVE", target, headers: [("Destination", pvBackup)]);   // original → backup (doc as source)
                await Ok("MOVE", pvTemp, headers: [("Destination", target)]);     // temp → target (the commit)
                await Ok("DELETE", pvBackup);
                break;

            case "Acrobat":
                // Adobe writes a temp then COPYs it over the target (copy-commit), then removes the temp.
                var acTemp = $"{basePath}/{name}.acrotmp{rand}";
                await Ok("PUT", acTemp, content);
                await Ok("COPY", acTemp, headers: [("Destination", target), ("Overwrite", "T")]);
                await Ok("DELETE", acTemp);
                break;

            case "Banana":
                var bnTemp = $"{basePath}/{name}.new{rand}";
                await Ok("PUT", bnTemp, content);
                await Ok("MOVE", bnTemp, headers: [("Destination", target)]);
                break;

            case "Chirp":
                await Ok("PUT", target, content); // direct in-place save
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(suite), suite, "unknown editor");
        }
    }
}
