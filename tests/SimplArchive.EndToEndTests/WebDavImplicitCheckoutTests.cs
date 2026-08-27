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

    // The OTHER atomic save (#762): rather than writing to a temporary NAME, a word processor creates a sibling
    // COLLECTION, works inside it, and swaps. Every verb of that sequence is driven here, because the fix for
    // the first one covered only the MKCOL and the sequence died on the very next request.
    //
    // Both obvious answers to the MKCOL were wrong, and the history is why this test drives the whole flow:
    // 403 made the editor conclude the volume cannot replace atomically, roll back, and DELETE THE ORIGINAL;
    // a bare 201 materialised nothing, so the write inside the collection could not resolve its parent and
    // returned 409 — "failed to write" — with the save lost. Accepting a request is a promise, and the promise
    // is kept by the verbs that come after it.
    [Fact]
    public async Task An_atomic_save_through_a_temporary_collection_stashes_the_edit()
    {
        var ctx = await SetupAsync();
        var (docId, docName) = await SeedDocumentAsync(ctx, ".docx", "original");

        var basePath = $"/webdav/{ctx.RepoName}";
        // The real shape: named after the file being replaced, hex and a random tail.
        var collection = $"{docName}.sb-a1b2c3d4-Xy9";

        // FIRST the editor probes for a FREE name, and it must be told the name is free. Measured on the wire:
        // it PROPFINDs candidate after candidate, incrementing the random tail, until one 404s —
        //     PROPFIND …/Test123.docx.sb-43a5b669-ZdBc5Y
        //     PROPFIND …/Test123.docx.sb-43a5b669-adBc5Y   …and on, and on
        // — so answering 207 "it exists" to every candidate means none is ever free, the editor never reaches
        // MKCOL, and saving hangs. This assertion exists to stop that answer being reintroduced: it looks
        // helpful, and it deadlocks the client.
        var probe = await DavAsync(ctx, "PROPFIND", $"{basePath}/{collection}", null, ("Depth", "0"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, probe.StatusCode);

        (await DavAsync(ctx, "MKCOL", $"{basePath}/{collection}")).EnsureSuccessStatusCode();

        // The write that used to fail. The leaf carries the ORIGINAL's name — which is the whole reason a
        // leaf-only test for the temp marker never matched it.
        var edited = Encoding.UTF8.GetBytes("edited atomically");
        var put = await DavAsync(ctx, "PUT", $"{basePath}/{collection}/{docName}", edited);
        Assert.True(put.IsSuccessStatusCode, $"the write inside the safe-save collection returned {(int)put.StatusCode}");

        // …and now it must be VISIBLE. Accepting a write and then answering 404 to "is it there?" is what
        // produced "Word cannot complete the save due to a file permission error": the editor concluded it
        // could not write. Measured on the wire — PUT 201, then PROPFIND 404 on the very file just accepted,
        // eight times, then the error. Both the collection and its contents are asserted, because the editor
        // asks about both.
        var listed = await DavAsync(ctx, "PROPFIND", $"{basePath}/{collection}", null, ("Depth", "1"));
        Assert.True(listed.IsSuccessStatusCode, $"PROPFIND on the created collection returned {(int)listed.StatusCode}");
        Assert.Contains(docName, await listed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var file = await DavAsync(ctx, "PROPFIND", $"{basePath}/{collection}/{docName}", null, ("Depth", "0"));
        Assert.True(file.IsSuccessStatusCode, $"PROPFIND on the staged file returned {(int)file.StatusCode}");

        // The swap, and then the editor tidies up.
        var move = await DavAsync(ctx, "MOVE", $"{basePath}/{collection}/{docName}", null, ("Destination", $"{basePath}/{docName}"));
        Assert.True(move.IsSuccessStatusCode, $"the committing swap returned {(int)move.StatusCode}");
        (await DavAsync(ctx, "DELETE", $"{basePath}/{collection}")).EnsureSuccessStatusCode();

        // Same outcome as save-by-rename, deliberately: checked out with the edit in the stash, never a silent
        // new version (ADR 0562). One act, one answer, whichever atomic-save shape the editor uses.
        var checkouts = await TestJson.Get(ctx.Api, "/api/checkouts");
        var row = checkouts.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == docId);
        Assert.Equal(EditorAgent, row.GetProperty("implicitAgent").GetString());
        Assert.True(row.GetProperty("isModified").GetBoolean());

        // …and no phantom folder left behind. A version-less Document is drawn as a FOLDER, which is what the
        // materialising answer produced: three saves, three of them.
        var children = (await TestJson.Get(ctx.Owner, $"/api/documents/{ctx.RepoId}/children"))
            .GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
        Assert.DoesNotContain(children, n => n.Contains(".sb-", StringComparison.Ordinal));
    }

    // The MOUNT ROOT still lists (#762). This is the first request any client makes, and the safe-save fix broke
    // it: the new PROPFIND branch asked `IsSafeSaveTemp(segments[^1])`, which throws on the EMPTY segment list
    // the root produces, so every mount attempt got a 500 before authentication had even been questioned.
    //
    // Caught by trying to mount, not by the suite — every existing WebDAV test addresses a path with segments
    // in it, so the one shape that has none was the one nothing exercised. Both spellings are driven because a
    // client sends both, and only the trailing-slash form yields the empty list.
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public async Task The_mount_root_lists(string trailing)
    {
        var ctx = await SetupAsync();
        await SeedDocumentAsync(ctx, ".docx", "original");

        var response = await DavAsync(ctx, "PROPFIND", $"/webdav{trailing}", null, ("Depth", "1"));
        Assert.True(response.IsSuccessStatusCode, $"PROPFIND on the mount root returned {(int)response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(ctx.RepoName, body, StringComparison.Ordinal);
    }

    // macOS's atomic save, REPLAYED from a real capture (#762). Every request below was taken from the WebDAV
    // Trace of an actual save, in order, with the same headers — `WebDAVFS/3.0.0 Darwin/24.6.0`, which is the
    // client that really talks to us: a word processor writes to a mounted volume and macOS turns POSIX calls
    // into WebDAV, so what arrives is nothing like what the editor "intended".
    //
    // It exists because nine rounds of diagnosis were spent asking a person to save a document and reading the
    // status codes afterwards. A capture replayed in a test costs nothing and can be iterated in seconds, and
    // not building one sooner was the most expensive mistake in this issue.
    //
    // The shape that matters: macOS CREATES THE FILE FIRST with a zero-byte PUT, then writes the content
    // elsewhere. Discard that create and everything after it is built on a file that does not exist.
    [Fact]
    public async Task The_macos_atomic_save_sequence_files_the_document()
    {
        var ctx = await SetupAsync();
        var basePath = $"/webdav/{ctx.RepoName}";
        const string name = "Replayed.docx";
        var collection = $"{name}.sb-43a5b669-1eNpWh";

        // 1. The file is created empty, before a byte of content exists.
        var create = await DavAsync(ctx, "PUT", $"{basePath}/{name}", []);
        Assert.True(create.IsSuccessStatusCode, $"the zero-byte create returned {(int)create.StatusCode}");

        // 2. …and is immediately readable. This is the step that used to answer 404 and break everything after.
        var readBack = await DavAsync(ctx, "GET", $"{basePath}/{name}");
        Assert.Equal(System.Net.HttpStatusCode.OK, readBack.StatusCode);

        var listed = await DavAsync(ctx, "PROPFIND", $"{basePath}/{name}", null, ("Depth", "0"));
        Assert.True(listed.IsSuccessStatusCode, $"PROPFIND on the placeholder returned {(int)listed.StatusCode}");

        // 3. The scratch collection, probed for a free name first.
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await DavAsync(ctx, "PROPFIND", $"{basePath}/{collection}", null, ("Depth", "0"))).StatusCode);
        (await DavAsync(ctx, "MKCOL", $"{basePath}/{collection}")).EnsureSuccessStatusCode();

        // 4. The AppleDouble sidecar beside it — accepted, and readable afterwards. A second PUT must say 204,
        //    not 201: a client told "created" twice concludes nothing is being kept and gives up.
        Assert.Equal(System.Net.HttpStatusCode.Created,
            (await DavAsync(ctx, "PUT", $"{basePath}/._{collection}", [])).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await DavAsync(ctx, "PUT", $"{basePath}/._{collection}", new byte[4096])).StatusCode);

        // 5. The real content, written INSIDE the collection under a Word temp name.
        var content = Encoding.UTF8.GetBytes("the saved document");
        var wordTemp = ".~WRD3576";
        Assert.True((await DavAsync(ctx, "PUT", $"{basePath}/{collection}/{wordTemp}", content)).IsSuccessStatusCode);
        Assert.True((await DavAsync(ctx, "PROPFIND", $"{basePath}/{collection}/{wordTemp}", null, ("Depth", "0"))).IsSuccessStatusCode);

        // 6. The SET-ASIDE. macOS moves the ORIGINAL into the scratch collection as a backup BEFORE renaming
        //    the new content into place — the opposite direction from the one this was first built for, and the
        //    step that made the file vanish: refused with 409, macOS concluded the save had failed and issued
        //    DELETE on the document. Accepting it must keep the document exactly where it is.
        var setAside = await DavAsync(ctx, "MOVE", $"{basePath}/{name}", null,
            ("Destination", $"{basePath}/{collection}/~WRL0328"), ("Overwrite", "T"));
        Assert.True(setAside.IsSuccessStatusCode, $"the set-aside returned {(int)setAside.StatusCode}");

        // The document must SURVIVE it. This is the assertion the whole issue turns on.
        Assert.True((await DavAsync(ctx, "GET", $"{basePath}/{name}")).IsSuccessStatusCode,
            "the original was lost when it was moved aside");

        // …and the backup we said we created must be READABLE, not merely listable. macOS reads back
        // everything it writes: four consecutive GETs on this path returned 404 after we answered 201 to the
        // MOVE, and Word showed "Saved" and then reverted. Listing was fixed several rounds before reading,
        // which is why GET was the last verb still contradicting the others.
        Assert.True((await DavAsync(ctx, "GET", $"{basePath}/{collection}/~WRL0328")).IsSuccessStatusCode,
            "the set-aside backup could not be read back");

        // Every other write we ACCEPTED is readable too — the sidecar and the staged content.
        Assert.True((await DavAsync(ctx, "GET", $"{basePath}/._{collection}")).IsSuccessStatusCode,
            "the AppleDouble sidecar could not be read back");
        Assert.True((await DavAsync(ctx, "GET", $"{basePath}/{collection}/{wordTemp}")).IsSuccessStatusCode,
            "the staged content could not be read back");

        // 7. The swap onto the file created in step 1.
        var move = await DavAsync(ctx, "MOVE", $"{basePath}/{collection}/{wordTemp}", null,
            ("Destination", $"{basePath}/{name}"), ("Overwrite", "T"));
        Assert.True(move.IsSuccessStatusCode, $"the swap returned {(int)move.StatusCode}");

        // 8. macOS also moves its AppleDouble sidecar out to the final name. Committing THAT minted documents
        //    called `._Test.docx` — 4 KB of resource-fork metadata filed as though it were someone's work. The
        //    clutter filter decides what may become a document, and it has to decide on EVERY path in, not just
        //    on PUT: a rule enforced at one entrance is not a rule.
        var sidecar = await DavAsync(ctx, "MOVE", $"{basePath}/{collection}/._{wordTemp}", null,
            ("Destination", $"{basePath}/._{name}"), ("Overwrite", "T"));
        Assert.True(sidecar.IsSuccessStatusCode, $"moving the sidecar out returned {(int)sidecar.StatusCode}");

        (await DavAsync(ctx, "DELETE", $"{basePath}/{collection}")).EnsureSuccessStatusCode();

        // THE ASSERTION THAT MATTERS: the saved bytes come back. Everything above answered 2xx while the file
        // read as zero bytes — asserting the CONTENT rather than the status code is the only way that shows up.
        var saved = await DavAsync(ctx, "GET", $"{basePath}/{name}");
        Assert.True(saved.IsSuccessStatusCode, $"reading the saved document returned {(int)saved.StatusCode}");
        Assert.Equal(content, await saved.Content.ReadAsByteArrayAsync());

        // …and the LISTING must agree with the download. Serving the working copy from GET while PROPFIND still
        // reported the checked-in length is what leaves Finder showing 0 bytes for a file that downloads fine.
        var props = await (await DavAsync(ctx, "PROPFIND", $"{basePath}/{name}", null, ("Depth", "0")))
            .Content.ReadAsStringAsync();
        Assert.Contains($"<D:getcontentlength>{content.Length}</D:getcontentlength>", props, StringComparison.Ordinal);

        // The save is a WORKING COPY, not a version (ADR 0562, reaffirmed for #762): the document is checked out
        // to its author and still has exactly the one empty version the placeholder created. Check-in — from the
        // Check-out tab — is what mints the next one.
        var row = (await TestJson.Get(ctx.Api, "/api/checkouts")).GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Replayed");
        Assert.True(row.GetProperty("isModified").GetBoolean());
        Assert.Single((await TestJson.Get(ctx.Owner, $"/api/documents/{row.GetProperty("id").GetGuid()}/versions"))
            .GetProperty("versions").EnumerateArray());

        // The document exists ONCE, with the content — not one document per step, and no phantom folder.
        var children = (await TestJson.Get(ctx.Owner, $"/api/documents/{ctx.RepoId}/children"))
            .GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
        Assert.Single(children, n => n == "Replayed");
        Assert.DoesNotContain(children, n => n.Contains(".sb-", StringComparison.Ordinal));

        // …and NOTHING the clutter filter keeps out arrived by another door.
        Assert.DoesNotContain(children, n => n.StartsWith("._", StringComparison.Ordinal));
        Assert.DoesNotContain(children, n => n.StartsWith("~$", StringComparison.Ordinal));
    }

    // Editing a document that ALREADY HAS CONTENT, in place. The create-new sequence above starts from an empty
    // placeholder, so its backup is legitimately empty and it cannot catch this: the set-aside backup has to
    // CONTAIN the document.
    //
    // An empty marker passed every status-code check and stopped the save dead — Word reads its backup back
    // before continuing, got `200, Content-Length: 0`, and refused rather than destroy the original. Correct of
    // it, wrong of us, and invisible to any assertion that only reads status codes.
    [Fact]
    public async Task Editing_in_place_sets_the_original_aside_with_its_content()
    {
        var ctx = await SetupAsync();
        var (_, docName) = await SeedDocumentAsync(ctx, ".docx", "the original content");

        var basePath = $"/webdav/{ctx.RepoName}";
        var collection = $"{docName}.sb-43a5b669-EdIt01";
        (await DavAsync(ctx, "MKCOL", $"{basePath}/{collection}")).EnsureSuccessStatusCode();

        var setAside = await DavAsync(ctx, "MOVE", $"{basePath}/{docName}", null,
            ("Destination", $"{basePath}/{collection}/~WRL1260"), ("Overwrite", "T"));
        Assert.True(setAside.IsSuccessStatusCode, $"the set-aside returned {(int)setAside.StatusCode}");

        // THE ASSERTION: the backup carries the document's bytes, not an empty marker.
        var backup = await DavAsync(ctx, "GET", $"{basePath}/{collection}/~WRL1260");
        Assert.True(backup.IsSuccessStatusCode, $"reading the backup returned {(int)backup.StatusCode}");
        Assert.Equal("the original content", await backup.Content.ReadAsStringAsync());

        // …and the document itself is untouched where it lives, because version history is the real backup.
        var still = await DavAsync(ctx, "GET", $"{basePath}/{docName}");
        Assert.True(still.IsSuccessStatusCode, "the original was lost when it was moved aside");
    }

    // A zero-byte write over a document that HAS content changes nothing. macOS opens a file for writing by
    // creating/truncating it first and sends the content in a second request, so an empty body is the OS
    // clearing its throat, not an edit.
    //
    // Treating it as one stashed an EMPTY working copy over the document — and because the tree serves the
    // owner their working copy, the file then read as 0 bytes while the archive still held every byte. Nothing
    // was lost and it looked exactly like loss, which is nearly as bad: the reporter's first conclusion was
    // "the docx has been wiped from S3".
    [Fact]
    public async Task A_zero_byte_write_does_not_empty_a_document()
    {
        var ctx = await SetupAsync();
        var (docId, docName) = await SeedDocumentAsync(ctx, ".docx", "content that must survive");

        var truncate = await DavAsync(ctx, "PUT", $"/webdav/{ctx.RepoName}/{docName}", []);
        Assert.True(truncate.IsSuccessStatusCode, $"the create/truncate returned {(int)truncate.StatusCode}");

        // Reading it back gives the content, not nothing.
        var read = await DavAsync(ctx, "GET", $"/webdav/{ctx.RepoName}/{docName}");
        Assert.Equal("content that must survive", await read.Content.ReadAsStringAsync());

        // …and it did not take a working copy: there was no edit to take one of.
        Assert.DoesNotContain((await TestJson.Get(ctx.Api, "/api/checkouts")).GetProperty("items").EnumerateArray(),
            c => c.GetProperty("id").GetGuid() == docId);
    }

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
