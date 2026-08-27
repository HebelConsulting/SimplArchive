using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SimplArchive.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

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
        var stash = await DavAsync(ctx, "GET", $"/SimplArchive/{Personal}/Check-out/{docName}");
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
        var stash = await DavAsync(ctx, "GET", $"/SimplArchive/{Personal}/Check-out/{docName}");
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

        var basePath = $"/SimplArchive/{ctx.RepoName}";
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

        var response = await DavAsync(ctx, "PROPFIND", $"/SimplArchive{trailing}", null, ("Depth", "1"));
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
        var basePath = $"/SimplArchive/{ctx.RepoName}";
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

        // The document must SURVIVE it — and survival is not the same as staying put. The path it moved FROM is
        // vacated, because that is what a MOVE means and the editor is about to rename the new content into it.
        // Answering 201 and leaving the document there was measured (#794) to end the save: the editor's own
        // identity followed the move it was told had happened, the name never came free, and it wrote nothing
        // more. What must not happen is LOSS, and the two assertions after this are what say so.
        Assert.Equal(HttpStatusCode.NotFound, (await DavAsync(ctx, "GET", $"{basePath}/{name}")).StatusCode);

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

        var basePath = $"/SimplArchive/{ctx.RepoName}";
        var collection = $"{docName}.sb-43a5b669-EdIt01";
        (await DavAsync(ctx, "MKCOL", $"{basePath}/{collection}")).EnsureSuccessStatusCode();

        var setAside = await DavAsync(ctx, "MOVE", $"{basePath}/{docName}", null,
            ("Destination", $"{basePath}/{collection}/~WRL1260"), ("Overwrite", "T"));
        Assert.True(setAside.IsSuccessStatusCode, $"the set-aside returned {(int)setAside.StatusCode}");

        // THE ASSERTION: the backup carries the document's bytes, not an empty marker.
        var backup = await DavAsync(ctx, "GET", $"{basePath}/{collection}/~WRL1260");
        Assert.True(backup.IsSuccessStatusCode, $"reading the backup returned {(int)backup.StatusCode}");
        Assert.Equal("the original content", await backup.Content.ReadAsStringAsync());

        // …the path it left is vacated, which is the half that makes the 201 true (#794) …
        Assert.Equal(HttpStatusCode.NotFound, (await DavAsync(ctx, "GET", $"{basePath}/{docName}")).StatusCode);

        // …and the ARCHIVE kept the document regardless. The set-aside is a claim about the mounted path, not
        // about the document: the row, its name and its confirmed version are untouched, so nothing outside
        // this mount — the tree, the search index, the other client — sees a gap at all.
        var children = (await TestJson.Get(ctx.Owner, $"/api/documents/{ctx.RepoId}/children"))
            .GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
        Assert.Contains(Path.GetFileNameWithoutExtension(docName), children);
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

        var truncate = await DavAsync(ctx, "PUT", $"/SimplArchive/{ctx.RepoName}/{docName}", []);
        Assert.True(truncate.IsSuccessStatusCode, $"the create/truncate returned {(int)truncate.StatusCode}");

        // Reading it back gives the content, not nothing.
        var read = await DavAsync(ctx, "GET", $"/SimplArchive/{ctx.RepoName}/{docName}");
        Assert.Equal("content that must survive", await read.Content.ReadAsStringAsync());

        // …and it did not take a working copy: there was no edit to take one of.
        Assert.DoesNotContain((await TestJson.Get(ctx.Api, "/api/checkouts")).GetProperty("items").EnumerateArray(),
            c => c.GetProperty("id").GetGuid() == docId);
    }

    // The macOS Intray sequence as CAPTURED, not as reasoned about (#794). Rebuilt from a WebDAV Trace of a
    // real save after the hand-written version below passed while the actual client failed — a replay built by
    // analogy with the tree is a replay of my assumptions.
    //
    // It asserts a property rather than a script: every request a filesystem client makes must get a COHERENT
    // answer. A probe for a free name may 404; a read of something we accepted may not. The first incoherent
    // answer is the defect, and listing them all in one run beats discovering them one save at a time.
    [Fact]
    public async Task Every_request_in_the_captured_intray_sequence_is_answered_coherently()
    {
        var ctx = await SetupAsync();
        var intray = $"/SimplArchive/{Personal}/Intray";
        const string doc = "Captured.docx";
        var col = $"{doc}.sb-43a5b669-OjK7I6";
        var content = Encoding.UTF8.GetBytes("the captured content");
        var sidecar = new byte[4096];

        // The captured order. Probes that legitimately 404 are marked; everything else must succeed.
        var steps = new (string Verb, string Path, byte[]? Body, string? Dest, bool MayBeMissing)[]
        {
            ("PROPFIND", $"{intray}/{doc}", null, null, true),          // does it exist yet? no
            ("PUT",      $"{intray}/{doc}", [], null, false),           // create it empty FIRST
            ("PROPFIND", $"{intray}/{doc}", null, null, false),         // …and it must exist now
            ("PUT",      $"{intray}/._{doc}", sidecar, null, false),    // the sidecar
            ("GET",      $"{intray}/._{doc}", null, null, false),       // …read back
            ("LOCK",     $"{intray}/{doc}", null, null, false),
            ("UNLOCK",   $"{intray}/{doc}", null, null, false),
            ("PROPFIND", $"{intray}/{col}", null, null, true),          // free-name probe
            ("MKCOL",    $"{intray}/{col}", null, null, false),
            ("PROPFIND", $"{intray}/{col}", null, null, false),         // …now it exists
            ("PUT",      $"{intray}/._{col}", sidecar, null, false),
            ("PROPFIND", $"{intray}/{col}/Contents", null, null, true), // macOS bundle probe
            ("PUT",      $"{intray}/{col}/.~WRD3279", [], null, false), // the temp, created EMPTY first
            ("PUT",      $"{intray}/{col}/._.~WRD3279", sidecar, null, false),
            ("LOCK",     $"{intray}/{col}/.~WRD3279", null, null, false),
            ("PUT",      $"{intray}/{col}/.~WRD3279", content, null, false),   // …then the content
            ("UNLOCK",   $"{intray}/{col}/.~WRD3279", null, null, false),
            ("GET",      $"{intray}/{col}/.~WRD3279", null, null, false),      // …readable
            ("GET",      $"{intray}/{doc}", null, null, false),                // the item, still empty
            ("MOVE",     $"{intray}/{doc}", null, $"{intray}/{col}/~WRL1263", false),      // set-aside
            ("GET",      $"{intray}/{col}/~WRL1263", null, null, false),                   // …backup readable
            ("MOVE",     $"{intray}/._{doc}", null, $"{intray}/{col}/._~WRL1263", false),  // sidecar aside
            ("MOVE",     $"{intray}/{col}/.~WRD3279", null, $"{intray}/{doc}", false),     // THE SWAP
            ("GET",      $"{intray}/{doc}", null, null, false),                            // …the saved bytes
            ("DELETE",   $"{intray}/{col}", null, null, false),
        };

        var incoherent = new List<string>();
        var heldTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (verb, path, body, dest, mayBeMissing) in steps)
        {
            var headers = new List<(string, string)> { ("Depth", "0"), ("Timeout", "Second-600") };
            if (dest is not null)
            {
                headers.Add(("Destination", dest));
                headers.Add(("Overwrite", "T"));
            }

            // UNLOCK carries the token its LOCK returned. Omitting it is a 409 the SERVER is right to give —
            // the first run of this replay reported two, and both were the test's omission rather than a
            // defect. A replay that does not speak the protocol correctly manufactures its own findings.
            if (verb == "UNLOCK" && heldTokens.TryGetValue(path, out var token))
            {
                headers.Add(("Lock-Token", $"<{token}>"));
            }

            var response = await DavAsync(ctx, verb, path, body, [.. headers]);
            if (verb == "LOCK" && response.Headers.TryGetValues("Lock-Token", out var issued))
            {
                heldTokens[path] = issued.First().Trim('<', '>');
            }

            var ok = response.IsSuccessStatusCode
                || (mayBeMissing && response.StatusCode == System.Net.HttpStatusCode.NotFound);
            if (!ok)
            {
                incoherent.Add($"{verb} {path.Replace(intray, "…")} → {(int)response.StatusCode}");
            }
        }

        Assert.True(incoherent.Count == 0,
            "the client would have been given an incoherent answer:\n  " + string.Join("\n  ", incoherent));

        // …and the save actually landed.
        Assert.Equal(content, await (await DavAsync(ctx, "GET", $"{intray}/{doc}")).Content.ReadAsByteArrayAsync());
    }

    // The macOS atomic save, replayed against the INTRAY (#794). Same client, same sequence, different surface:
    // the Intray is flat object-storage keys with no Documents, no versions and — the part that matters — no
    // soft-delete, which is where #762's original 403 destroyed a file a GET had served seconds earlier.
    //
    // Written BEFORE the implementation this time. In #762 the same sequence was discovered one verb at a time
    // over ten rounds of asking a person to save a document, because the editor only reaches a verb once the
    // previous one stops lying to it — so a trace shows what fails FIRST, never what is broken.
    [Fact]
    public async Task The_macos_atomic_save_sequence_works_in_the_intray()
    {
        var ctx = await SetupAsync();
        var intray = $"/SimplArchive/{Personal}/Intray";
        const string name = "Replayed.docx";
        var collection = $"{name}.sb-43a5b669-InTr01";
        var original = Encoding.UTF8.GetBytes("the original intray content");
        var edited = Encoding.UTF8.GetBytes("the edited intray content");

        // An item already staged in the Intray, as if dropped there earlier.
        (await DavAsync(ctx, "PUT", $"{intray}/{name}", original)).EnsureSuccessStatusCode();

        // 1. The scratch collection: probed for a free name, then created.
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await DavAsync(ctx, "PROPFIND", $"{intray}/{collection}", null, ("Depth", "0"))).StatusCode);
        (await DavAsync(ctx, "MKCOL", $"{intray}/{collection}")).EnsureSuccessStatusCode();
        Assert.True((await DavAsync(ctx, "PROPFIND", $"{intray}/{collection}", null, ("Depth", "0"))).IsSuccessStatusCode,
            "the created collection must exist");

        // 2. The AppleDouble sidecar beside it — a SECOND write of the same path must answer 204, not 201: a
        //    client told "created" twice concludes nothing is kept. Measured in the Intray trace as 201/201.
        Assert.Equal(System.Net.HttpStatusCode.Created, (await DavAsync(ctx, "PUT", $"{intray}/._{collection}", [])).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await DavAsync(ctx, "PUT", $"{intray}/._{collection}", new byte[4096])).StatusCode);

        // 3. A lock on an unmapped path inside the collection creates it: 201, per RFC 4918 §9.10.4.
        Assert.Equal(System.Net.HttpStatusCode.Created,
            (await DavAsync(ctx, "LOCK", $"{intray}/{collection}/.~WRD0001", null, ("Depth", "0"), ("Timeout", "Second-600"))).StatusCode);

        // 4. The content, written inside the collection, and readable back.
        Assert.True((await DavAsync(ctx, "PUT", $"{intray}/{collection}/.~WRD0001", edited)).IsSuccessStatusCode);
        Assert.Equal(edited, await (await DavAsync(ctx, "GET", $"{intray}/{collection}/.~WRD0001")).Content.ReadAsByteArrayAsync());

        // 5. The SET-ASIDE: the ORIGINAL moves into the collection as a backup, which must CONTAIN it — Word
        //    reads its backup before overwriting and refuses on an empty one.
        var setAside = await DavAsync(ctx, "MOVE", $"{intray}/{name}", null,
            ("Destination", $"{intray}/{collection}/~WRL0001"), ("Overwrite", "T"));
        Assert.True(setAside.IsSuccessStatusCode, $"the set-aside returned {(int)setAside.StatusCode}");
        Assert.Equal(original, await (await DavAsync(ctx, "GET", $"{intray}/{collection}/~WRL0001")).Content.ReadAsByteArrayAsync());

        // 6. The SIDECAR is set aside too, and moved back out afterwards. macOS does this for the AppleDouble
        //    beside the item — and it lives in the shadow area, not the Intray prefix, so a handler that only
        //    recognised real Intray items refused it with 403 and the whole save restarted (measured).
        (await DavAsync(ctx, "PUT", $"{intray}/._{name}", new byte[4096])).EnsureSuccessStatusCode();

        // A remembered write must be READABLE and DELETABLE here too, not just accepted. Storing it on PUT
        // while GET, PROPFIND and DELETE all answered 404 is what left the editor rewriting its sidecar and
        // never reaching the document — the write half alone is not the fix.
        Assert.True((await DavAsync(ctx, "GET", $"{intray}/._{name}")).IsSuccessStatusCode,
            "the remembered sidecar could not be read back");
        Assert.True((await DavAsync(ctx, "PROPFIND", $"{intray}/._{name}", null, ("Depth", "0"))).IsSuccessStatusCode,
            "the remembered sidecar was not listed");
        var sidecarAside = await DavAsync(ctx, "MOVE", $"{intray}/._{name}", null,
            ("Destination", $"{intray}/{collection}/._~WRL0001"), ("Overwrite", "T"));
        Assert.True(sidecarAside.IsSuccessStatusCode, $"the sidecar set-aside returned {(int)sidecarAside.StatusCode}");

        // 7. The swap, then the editor tidies up.
        var move = await DavAsync(ctx, "MOVE", $"{intray}/{collection}/.~WRD0001", null,
            ("Destination", $"{intray}/{name}"), ("Overwrite", "T"));
        Assert.True(move.IsSuccessStatusCode, $"the swap returned {(int)move.StatusCode}");

        // …and the sidecar comes back out WITHOUT becoming an Intray item: 4 KB of resource-fork metadata must
        // never be filed as though it were the user's work.
        var sidecarBack = await DavAsync(ctx, "MOVE", $"{intray}/{collection}/._~WRL0001", null,
            ("Destination", $"{intray}/._{name}"), ("Overwrite", "T"));
        Assert.True(sidecarBack.IsSuccessStatusCode, $"the sidecar move-out returned {(int)sidecarBack.StatusCode}");

        // The editor deletes its sidecar as part of tidying up; a 404 there reads as the save having failed.
        Assert.True((await DavAsync(ctx, "DELETE", $"{intray}/._{name}")).IsSuccessStatusCode,
            "deleting the remembered sidecar returned an error");
        (await DavAsync(ctx, "DELETE", $"{intray}/{collection}")).EnsureSuccessStatusCode();

        // The saved bytes come back — asserted as CONTENT, because every step above can answer 2xx while the
        // file reads as zero bytes.
        Assert.Equal(edited, await (await DavAsync(ctx, "GET", $"{intray}/{name}")).Content.ReadAsByteArrayAsync());

        // …and the DELETEd collection is gone from the listing. Its `._` sidecar legitimately remains listed
        // to its writer — the client wrote it beside the collection and never deleted it, and a listing that
        // omits what a direct request finds is the defect this issue is made of (#794). The sidecar's name
        // CONTAINS the collection's, so the absence is asserted on the collection's own href, not a substring.
        var listing = await (await DavAsync(ctx, "PROPFIND", intray, null, ("Depth", "1"))).Content.ReadAsStringAsync();
        Assert.Contains(name, listing, StringComparison.Ordinal);
        Assert.DoesNotContain($"Intray/{collection}", listing, StringComparison.Ordinal);
    }

    /// <summary>An AppleDouble sidecar FOR a scratch collection is a file, not the collection.</summary>
    /// <remarks>
    /// Taken from the wire, not reasoned about (#794). macOS writes `._&lt;name&gt;.sb-&lt;hex&gt;-&lt;rand&gt;`
    /// beside the scratch collection it just made — the sidecar carrying the DIRECTORY's metadata. Its name ends
    /// with the safe-save suffix because it is named after the collection, and `IsSafeSaveTemp` matches on the
    /// SUFFIX, so the `._` prefix changed nothing and the sidecar was classified as the collection itself.
    ///
    /// PUT then stored a file while PROPFIND looked for a collection marker, and the server gave two different
    /// answers about one path — measured as `PUT 4096B → 204` (updated: it exists) followed immediately by
    /// `PROPFIND → 404` (there is no such thing). macOS abandoned that collection and started another; the real
    /// 13 KB of content had already been written into the abandoned one, and the Intray kept the zero-byte
    /// placeholder from the very first `PUT … Content-Length: 0`.
    ///
    /// The assertion is the ADR 0707 promise, not a status code in isolation: what we ACCEPT must be readable
    /// back, and the listing must agree with the download.
    /// </remarks>
    [Fact]
    public async Task A_sidecar_named_after_a_scratch_collection_is_stored_and_served_as_a_file()
    {
        var ctx = await SetupAsync();
        var intray = $"/SimplArchive/{Personal}/Intray";

        // The exact shape from the trace: the sidecar is named after the collection, so it ENDS with the suffix.
        var collection = $"More testing.docx.sb-43a5b669-{Guid.NewGuid().ToString("N")[..6]}";
        var sidecar = $"{intray}/._{collection}";

        // macOS creates it empty first, then writes the 4 KB resource fork over it.
        Assert.Equal(HttpStatusCode.Created, (await DavAsync(ctx, "PUT", sidecar, [])).StatusCode);

        var fork = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var rewritten = await DavAsync(ctx, "PUT", sidecar, fork);
        Assert.True(rewritten.IsSuccessStatusCode, $"rewriting the sidecar returned {(int)rewritten.StatusCode}");

        // THE DEFECT: answered 204 above and 404 here. A path cannot be both.
        var props = await DavAsync(ctx, "PROPFIND", sidecar, null, ("Depth", "0"));
        Assert.True(props.IsSuccessStatusCode,
            $"PROPFIND on the sidecar we had just accepted returned {(int)props.StatusCode}");

        // …and it must come back as the bytes we took, not as an empty or absent resource.
        var read = await DavAsync(ctx, "GET", sidecar);
        Assert.True(read.IsSuccessStatusCode, $"GET on the sidecar returned {(int)read.StatusCode}");
        Assert.Equal(fork, await read.Content.ReadAsByteArrayAsync());

        // It is a SIDECAR, so it must never become an Intray ITEM — a staged object under the inbox prefix,
        // which is what the misclassification minted. It now legitimately appears in its writer's LISTING as a
        // remembered write (#794), so the assertion is on the store, where the two are distinguishable.
        using (var scope = _factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
            var inbox = $"tenants/{ctx.TenantId}/users/{await UserIdAsync(ctx)}/inbox/";
            Assert.DoesNotContain(
                await storage.ListObjectsAsync(inbox),
                o => o.Key[inbox.Length..].StartsWith("._", StringComparison.Ordinal));
        }
    }

    /// <summary>An Intray overwrite keeps the bytes it replaces, because nothing else here can.</summary>
    /// <remarks>
    /// Measured, in full (#794). A word processor saved into the Intray correctly — the swap landed, and the
    /// document read back at its true length for six seconds. Then it rolled its own save back by MOVEing an
    /// EMPTY backup slot over the file, and the user's work ceased to exist.
    ///
    /// The rollback is the SAME verb as the commit: an editor swaps a staged file onto the item either way, and
    /// the only difference is what happens to be in the staged file. So this cannot be decided by inspecting the
    /// request, and refusing the empty case would trade one defect for another — truncating a file is legitimate.
    /// What the gateway must not do is make the client's mistake FINAL, and in the Intray it uniquely could:
    /// a bare object with no version history and no soft-delete.
    ///
    /// Asserted against object storage rather than through the API, because there is deliberately no restore
    /// affordance yet — the bytes being recoverable is the whole claim, so the test verifies exactly that and
    /// nothing it cannot see.
    /// </remarks>
    [Fact]
    public async Task An_emptying_rollback_in_the_intray_keeps_the_bytes_it_replaced()
    {
        var ctx = await SetupAsync();
        var intray = $"/SimplArchive/{Personal}/Intray";
        var name = $"work{Guid.NewGuid().ToString("N")[..8]}.docx";
        var work = Encoding.UTF8.GetBytes("thirteen thousand bytes of somebody's afternoon");

        // The item, saved correctly — exactly as the trace showed it before the rollback.
        (await DavAsync(ctx, "PUT", $"{intray}/{name}", work)).EnsureSuccessStatusCode();
        Assert.Equal(work, await (await DavAsync(ctx, "GET", $"{intray}/{name}")).Content.ReadAsByteArrayAsync());

        // The rollback: a scratch collection holding an EMPTY backup slot, moved over the item.
        var collection = $"{name}.sb-43a5b669-{Guid.NewGuid().ToString("N")[..6]}";
        (await DavAsync(ctx, "MKCOL", $"{intray}/{collection}")).EnsureSuccessStatusCode();
        (await DavAsync(ctx, "PUT", $"{intray}/{collection}/~WRL2067", [])).EnsureSuccessStatusCode();
        var rollback = await DavAsync(ctx, "MOVE", $"{intray}/{collection}/~WRL2067", null, ("Destination", $"{intray}/{name}"));
        Assert.True(rollback.IsSuccessStatusCode, $"the rollback MOVE returned {(int)rollback.StatusCode}");

        // It is NOT refused — the client got what it asked for, and the item is now empty…
        Assert.Empty(await (await DavAsync(ctx, "GET", $"{intray}/{name}")).Content.ReadAsByteArrayAsync());

        // …but the work still exists. Without this the 13 KB were simply gone, with no version to fall back to.
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var previous = $"tenants/{ctx.TenantId}/users/{await UserIdAsync(ctx)}/inbox-previous/{name}";
        Assert.True(await storage.ExistsAsync(previous), "the bytes the rollback replaced were not kept");

        await using var kept = await storage.GetObjectAsync(previous);
        using var buffer = new MemoryStream();
        await kept.CopyToAsync(buffer);
        Assert.Equal(work, buffer.ToArray());
    }

    private static async Task<Guid> UserIdAsync(Context ctx) =>
        (await TestJson.Get(ctx.Api, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid();

    /// <summary>Editing in place changes the ETag, even when the new bytes are the same LENGTH.</summary>
    /// <remarks>
    /// The Intray taught this the expensive way (#794): an editor writes, then asks for `getetag` to confirm the
    /// write landed, and retries the whole save when it cannot. A tag that does not move is worse than an absent
    /// one — it is a positive claim that the file is unchanged.
    ///
    /// In the tree the tag is derived from the working copy's LENGTH but the DOCUMENT's modified time, and a
    /// save-in-place deliberately does not create a version (ADR 0562), so the document's timestamp need not
    /// move at all. Two saves of equal length therefore looked identical. Asserted with same-length content
    /// precisely because a differing length would hide it.
    /// </remarks>
    [Fact]
    public async Task Saving_in_place_changes_the_etag_even_when_the_length_is_unchanged()
    {
        var ctx = await SetupAsync();
        var (_, docName) = await SeedDocumentAsync(ctx, ".docx", "AAAA");
        var path = $"/SimplArchive/{ctx.RepoName}/{docName}";

        var before = await ETagAsync(ctx, path);
        Assert.False(string.IsNullOrWhiteSpace(before), "the document reported no getetag");

        await SaveByRenameAsync(ctx, docName, Encoding.UTF8.GetBytes("BBBB")); // same length, different bytes

        var after = await ETagAsync(ctx, path);
        Assert.NotEqual(before, after);

        // …and the download must agree with the listing, or a client validating by header sees something else.
        Assert.Equal(after, (await DavAsync(ctx, "GET", path)).Headers.ETag?.ToString());
    }

    private async Task<string?> ETagAsync(Context ctx, string path)
    {
        var response = await DavAsync(ctx, "PROPFIND", path, null, ("Depth", "0"));
        Assert.True(response.IsSuccessStatusCode, $"PROPFIND {path} returned {(int)response.StatusCode}");
        var xml = await response.Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(xml, "<D:getetag>(.*?)</D:getetag>");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>A rollback in the TREE keeps the in-flight edit it overwrites.</summary>
    /// <remarks>
    /// The same net as the Intray's, for a sequence measured on BOTH this branch and on main — so a standing
    /// hazard rather than a regression. An editor rolls its own save back by MOVEing an EMPTY backup slot onto
    /// the document (<c>MOVE …/~WRL2558 → the document</c>), and the commit and the rollback are the SAME verb
    /// carrying different bytes, so neither can be refused on inspection.
    ///
    /// Version history is not the answer, which is the trap this guards. A save in place writes the STASH and
    /// deliberately creates no version (ADR 0562): the confirmed version survives untouched while everything the
    /// user has done since their last check-in is what the empty write destroys.
    /// </remarks>
    [Fact]
    public async Task An_emptying_rollback_in_the_tree_keeps_the_working_copy_it_replaced()
    {
        var ctx = await SetupAsync();
        var (docId, docName) = await SeedDocumentAsync(ctx, ".docx", "original");
        var edit = Encoding.UTF8.GetBytes("an afternoon of edits nobody wants to retype");

        // A real save in place: the working copy now holds the user's edit.
        await SaveByRenameAsync(ctx, docName, edit);
        Assert.Equal(edit, await (await DavAsync(ctx, "GET", $"/SimplArchive/{Personal}/Check-out/{docName}")).Content.ReadAsByteArrayAsync());

        // The rollback: an empty scratch file moved onto the document, exactly as measured.
        var basePath = $"/SimplArchive/{ctx.RepoName}";
        var collection = $"{docName}.sb-43a5b669-{Guid.NewGuid().ToString("N")[..6]}";
        (await DavAsync(ctx, "MKCOL", $"{basePath}/{collection}")).EnsureSuccessStatusCode();
        (await DavAsync(ctx, "PUT", $"{basePath}/{collection}/~WRL2558", [])).EnsureSuccessStatusCode();
        var rollback = await DavAsync(ctx, "MOVE", $"{basePath}/{collection}/~WRL2558", null, ("Destination", $"{basePath}/{docName}"));
        Assert.True(rollback.IsSuccessStatusCode, $"the rollback MOVE returned {(int)rollback.StatusCode}");

        // Not refused — the working copy is now empty, which is what the client asked for…
        Assert.Empty(await (await DavAsync(ctx, "GET", $"/SimplArchive/{Personal}/Check-out/{docName}")).Content.ReadAsByteArrayAsync());

        // …but the edit still exists. No version was ever written, so nothing else could have kept it.
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var previous = $"tenants/{ctx.TenantId}/users/{await UserIdAsync(ctx)}/stash-previous/{docId:D}";
        Assert.True(await storage.ExistsAsync(previous), "the in-flight edit the rollback replaced was not kept");

        await using var kept = await storage.GetObjectAsync(previous);
        using var buffer = new MemoryStream();
        await kept.CopyToAsync(buffer);
        Assert.Equal(edit, buffer.ToArray());
    }

    // The same captured sequence, against a REPOSITORY FOLDER rather than the Intray (#794).
    //
    // Written after the location hypothesis was refuted on the live mount: saving a fresh document straight into
    // a shared repository fails exactly as it does in the personal space, and fails identically on `main`. So the
    // surface is not the variable and neither is any recent change — the tree has never served this sequence.
    //
    // It asserts the same PROPERTY as its Intray twin rather than a script: every request a filesystem client
    // makes gets a COHERENT answer. That matters more here than the final bytes, because the editor only reaches
    // a verb once the previous one stops lying to it — so a live trace shows what fails FIRST, never what is
    // broken, and ten rounds of asking a person to press Save is how #762 was found one verb at a time.
    [Fact]
    public async Task Every_request_in_the_captured_sequence_is_answered_coherently_in_the_tree()
    {
        var ctx = await SetupAsync();
        var repo = $"/SimplArchive/{ctx.RepoName}";
        const string doc = "Testing My Test.docx";
        var col = $"{doc}.sb-43a5b669-TrEe01";
        var content = Encoding.UTF8.GetBytes("the captured content, saved into the tree");
        var sidecar = new byte[4096];

        var steps = new (string Verb, string Path, byte[]? Body, string? Dest, bool MayBeMissing)[]
        {
            ("PROPFIND", $"{repo}/{doc}", null, null, true),           // does it exist yet? no
            ("PUT",      $"{repo}/{doc}", [], null, false),            // create it empty FIRST
            ("PROPFIND", $"{repo}/{doc}", null, null, false),          // …and it must exist now
            ("PUT",      $"{repo}/._{doc}", sidecar, null, false),     // the sidecar
            ("GET",      $"{repo}/._{doc}", null, null, false),        // …read back
            ("LOCK",     $"{repo}/{doc}", null, null, false),
            ("UNLOCK",   $"{repo}/{doc}", null, null, false),
            ("PROPFIND", $"{repo}/{col}", null, null, true),           // free-name probe
            ("MKCOL",    $"{repo}/{col}", null, null, false),
            ("PROPFIND", $"{repo}/{col}", null, null, false),          // …now it exists
            ("PUT",      $"{repo}/._{col}", sidecar, null, false),
            ("PROPFIND", $"{repo}/{col}/Contents", null, null, true),  // macOS bundle probe
            ("PUT",      $"{repo}/{col}/.~WRD3279", [], null, false),  // the temp, created EMPTY first
            ("PUT",      $"{repo}/{col}/._.~WRD3279", sidecar, null, false),
            ("LOCK",     $"{repo}/{col}/.~WRD3279", null, null, false),
            ("PUT",      $"{repo}/{col}/.~WRD3279", content, null, false),   // …then the content
            ("UNLOCK",   $"{repo}/{col}/.~WRD3279", null, null, false),
            ("GET",      $"{repo}/{col}/.~WRD3279", null, null, false),      // …readable
            ("GET",      $"{repo}/{doc}", null, null, false),                // the document, still empty
            ("MOVE",     $"{repo}/{doc}", null, $"{repo}/{col}/~WRL1263", false),      // set-aside
            ("GET",      $"{repo}/{col}/~WRL1263", null, null, false),                 // …backup readable
            ("MOVE",     $"{repo}/._{doc}", null, $"{repo}/{col}/._~WRL1263", false),  // sidecar aside
            ("MOVE",     $"{repo}/{col}/.~WRD3279", null, $"{repo}/{doc}", false),     // THE SWAP
            ("GET",      $"{repo}/{doc}", null, null, false),                          // …the saved bytes
            ("DELETE",   $"{repo}/{col}", null, null, false),
        };

        var incoherent = new List<string>();
        var heldTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (verb, path, body, dest, mayBeMissing) in steps)
        {
            var headers = new List<(string, string)> { ("Depth", "0"), ("Timeout", "Second-600") };
            if (dest is not null)
            {
                headers.Add(("Destination", dest));
                headers.Add(("Overwrite", "T"));
            }

            if (verb == "UNLOCK" && heldTokens.TryGetValue(path, out var token))
            {
                headers.Add(("Lock-Token", $"<{token}>"));
            }

            var response = await DavAsync(ctx, verb, path, body, [.. headers]);
            if (verb == "LOCK" && response.Headers.TryGetValues("Lock-Token", out var issued))
            {
                heldTokens[path] = issued.First().Trim('<', '>');
            }

            var ok = response.IsSuccessStatusCode || (mayBeMissing && response.StatusCode == HttpStatusCode.NotFound);
            if (!ok)
            {
                incoherent.Add($"{verb} {path.Replace(repo, "…", StringComparison.Ordinal)} → {(int)response.StatusCode}");
            }
        }

        Assert.True(incoherent.Count == 0,
            "the client would have been given an incoherent answer:\n  " + string.Join("\n  ", incoherent));

        // …and the save actually landed. The Intray keeps bytes; the tree keeps a WORKING COPY (ADR 0562), so
        // the document reads back through the mount while its confirmed version is deliberately untouched.
        Assert.Equal(content, await (await DavAsync(ctx, "GET", $"{repo}/{doc}")).Content.ReadAsByteArrayAsync());
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
        var basePath = $"/SimplArchive/{ctx.RepoName}";
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
