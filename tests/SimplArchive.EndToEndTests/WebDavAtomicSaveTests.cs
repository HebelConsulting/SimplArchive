using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// Atomic-save over the WebDAV special folders (ADR 0508). Real office apps never overwrite in place — they write
// a sibling TEMP file then rename it over the target (one major office suite additionally DELETEs the original and both
// office suites create a lock/owner file; macOS Preview renames the original away to a backup; some PDF tools
// commit via COPY). This drives each editor's exact save sequence, for every office format, against BOTH the
// Check-out folder (edits must land in the document's stash) and the Intray (edits must land in the staged object)
// — on real Postgres + object storage.
//
// Both test methods share a SINGLE tenant/user (created once) and give every round-trip unique file names — so the
// whole matrix adds just one per-tenant storage bucket, not one per case (which would exhaust the shared E2E
// server's bucket budget and 500 every later test).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class WebDavAtomicSaveTests
{
    // The personal space is named after its owner (ADR 0671), so its WebDAV/IMAP path segment is
    // whatever this test seeded as the display name — not the constant "Personal" it used to be.
    private const string Personal = "Dav User";

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

    // macOS safe-save: a word processor replaces a file atomically by creating a SIBLING COLLECTION named
    // `<file>.sb-<hex>-<rand>`, working inside it, then swapping (#764).
    //
    // Both obvious answers to that MKCOL are wrong, and this pins the one that is not. Materialising it leaves a
    // version-less Document, which the workbench draws as a FOLDER — three saves, three phantom folders. And
    // REFUSING it is worse: the editor concludes the volume cannot replace atomically, rolls back, and DELETES
    // the original. Observed on the kiosk destroying an Intray file that a GET had served two seconds earlier —
    // unrecoverably, because Intray items are storage keys with no soft-delete.
    [Fact]
    public async Task A_word_processors_safe_save_collection_is_accepted_and_leaves_nothing_behind()
    {
        var ctx = await SetupAsync();

        Task<HttpResponseMessage> Dav(string method, string path, byte[]? body = null)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = ctx.Basic } };
            if (body is not null) req.Content = new ByteArrayContent(body);
            return ctx.Dav.SendAsync(req);
        }

        var id = Guid.NewGuid().ToString("N")[..8];

        // THE INTRAY, where refusing this cost a file. Stage one, then run the editor's opening move against it.
        var intrayName = $"safe{id}.docx";
        (await Dav("PUT", $"/SimplArchive/{Personal}/Intray/{intrayName}", Encoding.UTF8.GetBytes("original"))).EnsureSuccessStatusCode();

        var intrayMkcol = await Dav("MKCOL", $"/SimplArchive/{Personal}/Intray/{intrayName}.sb-dea8d513-{id}");
        Assert.Equal(HttpStatusCode.Created, intrayMkcol.StatusCode);

        // The file the editor was replacing is STILL THERE. This is the assertion the bug was about: it is what
        // fails if the MKCOL is refused and the editor rolls back over it.
        var stillThere = await Dav("GET", $"/SimplArchive/{Personal}/Intray/{intrayName}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
        Assert.Equal("original", await stillThere.Content.ReadAsStringAsync());

        // A REPOSITORY folder, where the same MKCOL used to materialise a phantom folder.
        var folder = $"sf{id}";
        (await Dav("MKCOL", $"/SimplArchive/{await RepoNameAsync(ctx)}/{folder}")).EnsureSuccessStatusCode();

        var repoMkcol = await Dav("MKCOL", $"/SimplArchive/{await RepoNameAsync(ctx)}/{folder}/doc{id}.docx.sb-dea8d513-{id}");
        Assert.Equal(HttpStatusCode.Created, repoMkcol.StatusCode);

        // Accepted, and NOT materialised as a document. The WRITER's own listing now shows the in-flight
        // collection — deliberately (#794): every scratch path answers a direct request, and a listing that
        // denied it is what made the OS drop its cache mid-save and the editor abandon the collection. What
        // must still be true is that no DOCUMENT came of it, which the archive's own listing says.
        var listing = await Dav("PROPFIND", $"/SimplArchive/{await RepoNameAsync(ctx)}/{folder}/");
        var body = await listing.Content.ReadAsStringAsync();
        Assert.Contains($".sb-dea8d513-{id}", body, StringComparison.Ordinal);

        var folderId = (await TestJson.Get(ctx.Owner, $"/api/documents/{ctx.RepoId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == folder).GetProperty("id").GetGuid();
        Assert.Empty((await TestJson.Get(ctx.Owner, $"/api/documents/{folderId}/children"))
            .GetProperty("children").EnumerateArray());
    }

    // OS clutter is accepted-and-discarded at EVERY level of the mount — the root included (#794).
    //
    // The rule itself is old (ADR "WebDAV clutter filter") and was enforced everywhere except the one place
    // macOS writes first: `PUT /SimplArchive/.DS_Store` hit the "can't PUT at the repository-list root" refusal
    // that sat ABOVE the clutter check, so the identical file was accepted one level down and refused with 403
    // at the top. That 403 was the only non-2xx in a ninety-second trace of a save that failed, and a refusal at
    // the root does not read as "not that file" — it reads as "this volume does not take writes". Afterwards the
    // editor stopped attempting an atomic replace at all: five scratch collections, an empty backup slot in each,
    // and the document never written into any of them.
    //
    // Parameterised over DEPTH rather than asserted at the root alone, because the defect was the difference
    // between the levels. A test that only covered the root would pass again the moment someone re-ordered the
    // guards the other way.
    [Theory]
    [InlineData(0)] // the mount root — where Finder writes first, and where this was broken
    [InlineData(1)] // a repository root
    [InlineData(2)] // a folder inside a repository
    public async Task Os_clutter_is_accepted_read_back_and_deletable_at_every_depth(int depth)
    {
        var ctx = await SetupAsync();
        var repo = await RepoNameAsync(ctx);
        var folder = $"Folder{Guid.NewGuid():N}"[..12];
        await TestJson.Post(ctx.Owner, $"/api/documents/{ctx.RepoId}/children", new { name = folder });

        var prefix = depth switch
        {
            0 => "/SimplArchive",
            1 => $"/SimplArchive/{repo}",
            _ => $"/SimplArchive/{repo}/{folder}",
        };

        var path = $"{prefix}/.DS_Store";
        var content = Encoding.UTF8.GetBytes("Finder's own bookkeeping, which is none of the archive's business");

        // Accepted. Not stored as a document — but not refused either, which is the whole point.
        var put = await ClutterDavAsync(ctx, "PUT", path, content);
        Assert.True(put.IsSuccessStatusCode, $"PUT {path} → {(int)put.StatusCode}; a refusal here reads as a read-only volume");

        // …and readable back, byte for byte. Accepting a write and then 404-ing a read of it is the same lie in
        // its other direction, and it is what the shadow area exists to prevent.
        var get = await ClutterDavAsync(ctx, "GET", path);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(content, await get.Content.ReadAsByteArrayAsync());

        // …and visible to the verb a filesystem client actually uses to look.
        var propfind = await ClutterDavAsync(ctx, "PROPFIND", path, null, ("Depth", "0"));
        Assert.Equal(207, (int)propfind.StatusCode);

        // …and removable, because the OS tidies its own junk and a 404 there reads as the volume losing writes.
        var delete = await ClutterDavAsync(ctx, "DELETE", path);
        Assert.True(delete.IsSuccessStatusCode, $"DELETE {path} → {(int)delete.StatusCode}");

        // It never became a document: the repository still holds only the folder seeded above.
        if (depth > 0)
        {
            var children = await TestJson.Get(ctx.Owner, $"/api/documents/{ctx.RepoId}/children");
            Assert.DoesNotContain(children.GetProperty("children").EnumerateArray(),
                c => c.GetProperty("name").GetString()!.Contains("DS_Store", StringComparison.Ordinal));
        }
    }

    // A document answers ONLY at the name the mount shows for it (#794).
    //
    // A file's WebDAV name is its Name plus the current version's extension; its Name alone is the stem. The old
    // lookup matched the stem first (for folders, whose WebDAV name IS their Name), so `Report` resolved to
    // `Report.docx` and a 207 came back whose href and displayname disagreed. That is already wrong under
    // RFC 4918, and an editor probing that exact name to find out whether it is FREE was told it was taken.
    [Fact]
    public async Task A_document_does_not_answer_at_its_name_without_the_extension()
    {
        var ctx = await SetupAsync();
        var repo = await RepoNameAsync(ctx);
        var (_, stem) = await SeedTreeDocumentAsync(ctx, Encoding.UTF8.GetBytes("the real document"));

        var real = await ClutterDavAsync(ctx, "PROPFIND", $"/SimplArchive/{repo}/{stem}.docx", null, ("Depth", "0"));
        Assert.Equal(207, (int)real.StatusCode);

        var bare = await ClutterDavAsync(ctx, "PROPFIND", $"/SimplArchive/{repo}/{stem}", null, ("Depth", "0"));
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
    }

    // The set-aside half of a macOS atomic replace is ANSWERED, not merely accepted (#794).
    //
    // The editor moves the original into its scratch collection as a backup and then renames the new content
    // into the name that just came free. We answer 201 to that move — refusing it was measured in #762 to make
    // the editor delete the original — but we do not re-parent an archived document into a temp folder. So the
    // mount reports the vacated path as gone while the archive keeps the row.
    //
    // Answering 201 and changing nothing was the defect: measured on the wire, the editor's own file identity
    // followed the move (its window retitled itself `~WRL0768`), the name it needed never came free, and it
    // issued no further writes at all. Every response in that exchange was a 2xx and the user's edit was lost.
    [Fact]
    public async Task A_set_aside_empties_the_path_it_moved_from_and_the_swap_fills_it_again()
    {
        var ctx = await SetupAsync();
        var repo = await RepoNameAsync(ctx);
        var original = Encoding.UTF8.GetBytes("the original content");
        var (docId, stem) = await SeedTreeDocumentAsync(ctx, original);

        var doc = $"/SimplArchive/{repo}/{stem}.docx";
        var collection = $"{doc}.sb-43a5b669-SetAs1";
        Assert.True((await ClutterDavAsync(ctx, "MKCOL", collection)).IsSuccessStatusCode);

        // The editor's new content, written inside the scratch collection under the original's own name.
        var edited = Encoding.UTF8.GetBytes("the edited content, which must survive");
        Assert.True((await ClutterDavAsync(ctx, "PUT", $"{collection}/{stem}.docx", edited)).IsSuccessStatusCode);

        // THE SET-ASIDE.
        var aside = await ClutterDavAsync(ctx, "MOVE", doc, null, ("Destination", $"{collection}/~WRL0768"), ("Overwrite", "T"));
        Assert.True(aside.IsSuccessStatusCode, $"MOVE aside → {(int)aside.StatusCode}");

        // The path it moved FROM is now gone — to every verb, and in the parent's listing. A path that is
        // missing to one verb and present to another is the shape of defect this whole issue is made of.
        Assert.Equal(HttpStatusCode.NotFound, (await ClutterDavAsync(ctx, "PROPFIND", doc, null, ("Depth", "0"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClutterDavAsync(ctx, "GET", doc)).StatusCode);

        // On the document's OWN href: the scratch collection is named after the document, so a bare substring
        // check would false-positive on the collection the listing now legitimately shows its writer (#794).
        var listing = await ClutterDavAsync(ctx, "PROPFIND", $"/SimplArchive/{repo}", null, ("Depth", "1"));
        Assert.DoesNotContain($"{stem}.docx</D:href>", await listing.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // …and the bytes are where the editor moved them, which is the reason it moved them.
        var backup = await ClutterDavAsync(ctx, "GET", $"{collection}/~WRL0768");
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        Assert.Equal(original, await backup.Content.ReadAsByteArrayAsync());

        // The ARCHIVE never lost it: the row, its name and its confirmed version are untouched throughout. The
        // set-aside is a claim about the mounted path, not about the document.
        var still = await TestJson.Get(ctx.Owner, $"/api/documents/{docId}");
        Assert.Equal(stem, still.GetProperty("name").GetString());

        // THE SWAP: the new content is renamed into the vacated name, and the path exists again — carrying the
        // edit, served from the working copy (ADR 0562: a save in place writes no version).
        var swap = await ClutterDavAsync(ctx, "MOVE", $"{collection}/{stem}.docx", null, ("Destination", doc), ("Overwrite", "T"));
        Assert.True(swap.IsSuccessStatusCode, $"MOVE swap → {(int)swap.StatusCode}");

        var reread = await ClutterDavAsync(ctx, "GET", doc);
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
        Assert.Equal(edited, await reread.Content.ReadAsByteArrayAsync());
    }

    // A sidecar written INSIDE a safe-save collection is one resource to every verb (#794).
    //
    // PUT, GET, LOCK and DELETE each choose where a swallowed write lives, and they must choose the SAME place:
    // `IsUnderSafeSaveTemp ? FileKey : ShadowKey`. A `._` name matches both that rule and the OS-clutter rule,
    // so it is the one name where the two can disagree — and when a clutter check was hoisted above the
    // safe-save branch, PUT stored it in the shadow area while LOCK and GET looked for it with the collection.
    //
    // The wire named it exactly: `PUT … → 201` then `LOCK … → 201`. A 201 from LOCK means the resource did not
    // exist and the lock created it (RFC 4918 §9.10), which contradicts the PUT that had just made it — so the
    // STATUS CODE is asserted here, not merely success. The editor's response to that contradiction was to
    // rewrite the same 4 KB sidecar four times and abandon the save.
    [Fact]
    public async Task A_sidecar_inside_a_safe_save_collection_is_one_resource_to_every_verb()
    {
        var ctx = await SetupAsync();
        var repo = await RepoNameAsync(ctx);
        var (_, stem) = await SeedTreeDocumentAsync(ctx, Encoding.UTF8.GetBytes("the original"));

        var collection = $"/SimplArchive/{repo}/{stem}.docx.sb-43a5b669-Sidec1";
        Assert.True((await ClutterDavAsync(ctx, "MKCOL", collection)).IsSuccessStatusCode);

        var sidecar = $"{collection}/._.~WRD1980";
        var bytes = new byte[4096];
        Array.Fill(bytes, (byte)7);

        // Created by the PUT …
        Assert.Equal(HttpStatusCode.Created, (await ClutterDavAsync(ctx, "PUT", sidecar, [])).StatusCode);

        // … so the LOCK finds it. 200 = "locked what was there"; 201 would mean the lock had to create it,
        // which is the server contradicting its own 201 above.
        var locked = await ClutterDavAsync(ctx, "LOCK", sidecar, null, ("Depth", "0"), ("Timeout", "Second-600"));
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var token = locked.Headers.GetValues("Lock-Token").First().Trim('<', '>');

        // The content write is a REPLACE, so 204 — not another 201, which would prove the first was not kept.
        Assert.Equal(HttpStatusCode.NoContent,
            (await ClutterDavAsync(ctx, "PUT", sidecar, bytes, ("If", $"(<{token}>)"))).StatusCode);

        // …and it reads back, byte for byte, at the same path it was written to.
        var read = await ClutterDavAsync(ctx, "GET", sidecar);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(bytes, await read.Content.ReadAsByteArrayAsync());

        // …the listing agrees with the download, rather than reporting the length of an empty first write.
        var props = await (await ClutterDavAsync(ctx, "PROPFIND", sidecar, null, ("Depth", "0"))).Content.ReadAsStringAsync();
        Assert.Contains($"<D:getcontentlength>{bytes.Length}</D:getcontentlength>", props, StringComparison.Ordinal);

        Assert.True((await ClutterDavAsync(ctx, "UNLOCK", sidecar, null, ("Lock-Token", $"<{token}>"))).IsSuccessStatusCode);

        // …and DELETE removes the thing it says it removed, rather than a key nobody wrote to.
        Assert.True((await ClutterDavAsync(ctx, "DELETE", sidecar)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClutterDavAsync(ctx, "GET", sidecar)).StatusCode);
    }

    // Mid-save, the folder's listing AGREES with the direct answers — for the user saving (#794).
    //
    // The last hiding place of the defect this issue is made of. Every scratch path — the collection, the
    // sidecar — answered PROPFIND, GET and LOCK directly while the folder's Depth-1 listing denied it existed.
    // Measured live: the OS re-enumerated the folder mid-save, its cache dropped the collection the editor was
    // standing in, and the editor abandoned it and opened another, forever — never writing the document's bytes
    // into any of them. Five verbs were made coherent one at a time before this; the LISTING is a verb too.
    //
    // Scoped to the WRITER: the scratch tiers are per-user, so a colleague listing the same folder sees it
    // clean. Both halves are asserted, because either alone can regress without the other noticing.
    [Fact]
    public async Task Mid_save_the_folder_listing_shows_the_writer_their_own_scratch_and_a_colleague_nothing()
    {
        var ctx = await SetupAsync();
        var repo = await RepoNameAsync(ctx);
        var (_, stem) = await SeedTreeDocumentAsync(ctx, Encoding.UTF8.GetBytes("the original"));

        // A second user in the same tenant, who can see the repository but shares no scratch area.
        var email = $"peer-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(ctx.TenantId, email, "peer-1234", "Peer User");
        await _factory.GrantTenantAdminAsync(email);
        var peerApi = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "peer-1234"));
        var peerDavPassword = (await TestJson.Post(peerApi, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var peerBasic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{peerDavPassword}")));

        // The dance, up to the point the live captures show the OS re-listing the folder: collection made,
        // collection sidecar written beside it, content temp inside it.
        var collection = $"{stem}.docx.sb-43a5b669-LiSt01";
        Assert.True((await ClutterDavAsync(ctx, "MKCOL", $"/SimplArchive/{repo}/{collection}")).IsSuccessStatusCode);
        Assert.True((await ClutterDavAsync(ctx, "PUT", $"/SimplArchive/{repo}/._{collection}", new byte[4096])).IsSuccessStatusCode);
        Assert.True((await ClutterDavAsync(ctx, "PUT", $"/SimplArchive/{repo}/{collection}/.~WRD0001", Encoding.UTF8.GetBytes("staged"))).IsSuccessStatusCode);

        // The writer's Depth-1 listing carries what the writer's direct requests find.
        var mine = await (await ClutterDavAsync(ctx, "PROPFIND", $"/SimplArchive/{repo}", null, ("Depth", "1")))
            .Content.ReadAsStringAsync();
        Assert.Contains(collection, mine, StringComparison.Ordinal);
        Assert.Contains($"._{collection}", mine, StringComparison.Ordinal);

        // A colleague's listing of the same folder, at the same moment, is clean.
        var peerRequest = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/SimplArchive/{repo}")
        {
            Headers = { Authorization = peerBasic },
        };
        peerRequest.Headers.TryAddWithoutValidation("Depth", "1");
        var theirs = await (await ctx.Dav.SendAsync(peerRequest)).Content.ReadAsStringAsync();
        Assert.Contains($"{stem}.docx", theirs, StringComparison.Ordinal);
        Assert.DoesNotContain(".sb-", theirs, StringComparison.Ordinal);
        Assert.DoesNotContain("/._", theirs, StringComparison.Ordinal);
    }

    // An editor's owner file in the tree is one resource to every verb — including the DELETE that removes it
    // when the editor closes (#794).
    //
    // The third instance of the split-key defect, and the wire named it the same way as the second: for
    // `~$xyz.docx` in a repository folder, LOCK wrote its lock-null under the SHADOW key (`LOCK → 201`) while
    // PUT stored the real owner file in the tree scratch, and DELETE checked neither — so Word, closing, was
    // told 404 for its own lock file and left it behind, listed, forever. The lifecycle below is the measured
    // one, with the status codes asserted exactly, because success-or-not cannot tell a 201 from a 200.
    [Fact]
    public async Task An_owner_file_in_the_tree_is_created_locked_read_and_deleted_as_one_resource()
    {
        var ctx = await SetupAsync();
        var repo = await RepoNameAsync(ctx);
        var (_, stem) = await SeedTreeDocumentAsync(ctx, Encoding.UTF8.GetBytes("the document"));

        var owner = $"/SimplArchive/{repo}/~${stem}.docx";
        var content = Encoding.UTF8.GetBytes("owner-file payload: who has it open");

        // LOCK first — the measured order. Word reserves the name before writing it; the lock-null it creates
        // IS the resource from that moment on.
        var locked = await ClutterDavAsync(ctx, "LOCK", owner, null, ("Depth", "0"), ("Timeout", "Second-600"));
        Assert.Equal(HttpStatusCode.Created, locked.StatusCode);
        var token = locked.Headers.GetValues("Lock-Token").First().Trim('<', '>');

        // The PUT lands on the SAME resource the LOCK created: 204, not another 201.
        Assert.Equal(HttpStatusCode.NoContent,
            (await ClutterDavAsync(ctx, "PUT", owner, content, ("If", $"(<{token}>)"))).StatusCode);

        // …and reads back as itself, from every verb.
        Assert.Equal(207, (int)(await ClutterDavAsync(ctx, "PROPFIND", owner, null, ("Depth", "0"))).StatusCode);
        var read = await ClutterDavAsync(ctx, "GET", owner);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(content, await read.Content.ReadAsByteArrayAsync());

        Assert.True((await ClutterDavAsync(ctx, "UNLOCK", owner, null, ("Lock-Token", $"<{token}>"))).IsSuccessStatusCode);

        // The editor closes and tidies up. This is the request that answered 404 and left the file behind.
        var delete = await ClutterDavAsync(ctx, "DELETE", owner);
        Assert.True(delete.IsSuccessStatusCode, $"DELETE {owner} → {(int)delete.StatusCode}; the editor cannot clean up after itself");
        Assert.Equal(HttpStatusCode.NotFound, (await ClutterDavAsync(ctx, "GET", owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClutterDavAsync(ctx, "PROPFIND", owner, null, ("Depth", "0"))).StatusCode);

        // …and it was never in the folder listing to begin with: an owner file says WHO has a document open,
        // which the special folders have always kept out of their listings — the same deliberate hiding, here.
        var listing = await (await ClutterDavAsync(ctx, "PROPFIND", $"/SimplArchive/{repo}", null, ("Depth", "1")))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("~%24", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("~$", listing, StringComparison.Ordinal);
    }

    // A document in the tree with real bytes, created the way the API does it so the version is Confirmed
    // (never hand-written — a hand-made Confirmed version dies on a CHECK constraint).
    private async Task<(Guid Id, string Stem)> SeedTreeDocumentAsync(Context ctx, byte[] content)
    {
        var stem = $"doc{Guid.NewGuid().ToString("N")[..8]}";
        var docId = (await TestJson.Post(ctx.Owner, $"/api/documents/{ctx.RepoId}/children", new { name = stem })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(ctx.Owner, $"/api/documents/{docId}/versions", new { fileExtension = ".docx" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(content))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(ctx.Owner, $"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });
        return (docId, stem);
    }

    private Task<HttpResponseMessage> ClutterDavAsync(
        Context ctx, string method, string path, byte[]? body = null, params (string Key, string Value)[] headers)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = ctx.Basic } };
        request.Headers.TryAddWithoutValidation("User-Agent", "WebDAVFS/3.0.0 (03008000) Darwin/24.6.0 (arm64)");
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

    private async Task<string> RepoNameAsync(Context ctx)
        => (await TestJson.Get(ctx.Owner, $"/api/documents/{ctx.RepoId}")).GetProperty("name").GetString()!;

    private sealed record Context(HttpClient Owner, HttpClient Api, Guid RepoId, Guid TenantId, AuthenticationHeaderValue Basic, HttpClient Dav);

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
        return new Context(owner, api, repoId, tenantId, basic, _factory.CreateClient());
    }

    // One full round-trip for a (format, editor): a freshly checked-out document edited via the Check-out folder
    // (→ its stash) and a staged Intray file edited via the Intray (→ the staged object). Unique names per call.
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
        var checkoutBack = await Dav("GET", $"/SimplArchive/{Personal}/Check-out/co{id}{ext}");
        Assert.Equal(HttpStatusCode.OK, checkoutBack.StatusCode);
        Assert.Equal(checkoutEdit, await checkoutBack.Content.ReadAsByteArrayAsync());

        // Intray: a staged file, edited via WebDAV → the staged object must hold the edit.
        var intrayName = $"in{id}{ext}";
        (await Dav("PUT", $"/SimplArchive/{Personal}/Intray/{intrayName}", Encoding.UTF8.GetBytes("original"))).EnsureSuccessStatusCode();

        var intrayEdit = Encoding.UTF8.GetBytes($"intray {suite} {ext} {id}");
        await SaveAtomicallyAsync(Dav, "Intray", intrayName, intrayEdit, suite);
        var intrayBack = await Dav("GET", $"/SimplArchive/{Personal}/Intray/{intrayName}");
        Assert.Equal(HttpStatusCode.OK, intrayBack.StatusCode);
        Assert.Equal(intrayEdit, await intrayBack.Content.ReadAsByteArrayAsync());
    }

    // Emits one editor's atomic-save sequence saving `content` onto /SimplArchive/{Personal}/{folder}/{name}, asserting
    // every WebDAV op succeeds (2xx). Temp names are unique per call.
    private static async Task SaveAtomicallyAsync(Func<string, string, byte[]?, (string, string)[]?, Task<HttpResponseMessage>> dav, string folder, string name, byte[] content, string suite)
    {
        var basePath = $"/SimplArchive/{Personal}/{folder}";
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
