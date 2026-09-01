using System.Net.Http.Json;
using System.Text;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MailKit.Net.Imap;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// Notes over IMAP (#564/#596/#780), driven end to end with a real mail-client library against the hosted
// TcpListener: the notebook tree that CREATE brings into being, UUID-correlated versioning, typed containment,
// what LIST advertises as creatable, and the text/html round trip a notes client depends on.
//
// Split out of ImapEndpointTests (the 1000-line debt list), which was 1,127 lines covering six unrelated
// subjects at once. This file is one of them, and the split follows the convention the directory already had:
// ImapSearchTests, ImapStandingMailboxTests, ImapAttachmentFetchTests and ImapReferenceMailboxTests were each
// already their own file — ImapEndpointTests was simply the one that never got divided.
//
// Same [Collection] as its siblings, so it shares the one E2EApiFactory rather than standing up a second host.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ImapNotesTests
{
    private readonly E2EApiFactory _factory;

    public ImapNotesTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Notes_sync_with_uuid_correlated_versioning_and_typed_containment()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-note-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "note-1234", "Note Writer");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "note-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        // The notebook is not provisioned and does not sit loose in Personal: it lives under the MAILBOX, and
        // generating the IMAP credential above has already materialised that (the second of the two triggers,
        // #562). Creating the notebook there is what a notes client's `CREATE "Notes"` does.
        //
        // The TREE says "Notebook" while IMAP says "Notes", and holding both in one test is the point: they
        // are one folder with two projections, so the wire name a notes client looks for must survive a
        // rename of what the workbench displays (#564).
        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var personalId = personal.GetProperty("id").GetGuid();
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox")
            .GetProperty("id").GetGuid();

        await TestJson.Post(api, $"/api/documents/{mailboxId}/children",
            new { name = "Notebook", folderMask = "notes" });

        // Read back from the LISTING rather than trusting the create response: documentType is what the two
        // clients key their type column off, and the create response does not carry it.
        var notes = (await TestJson.Get(api, $"/api/documents/{mailboxId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Notebook");
        Assert.Equal("Notebook", notes.GetProperty("documentType").GetString());
        var notesId = notes.GetProperty("id").GetGuid();

        // Typed containment: a non-Note document cannot LIVE in the notebook (the SaveChanges invariant).
        //
        // Asked for as a FOLDER — a bare create no longer proves this and must not (ADR 0623): the endpoint
        // serves both "make a folder" and step one of an upload with the same body, so inside a typed folder a
        // bare create is an item-to-be that the finalizer classifies. Naming the mask is the unambiguous ask,
        // and it earns the reason rather than a name conflict for a name that is free.
        var refused = await api.PostAsJsonAsync(
            $"/api/documents/{notesId}/children", new { name = "not-a-note", folderMask = "folder" });
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("TYPED_FOLDER_CONTAINMENT", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        // One folder, two projections: IMAP shows a ROOT-level "Notes", and NOT an INBOX/Notes child.
        var all = await client.GetFoldersAsync(client.PersonalNamespaces[0]);
        Assert.Contains(all, f => f.FullName == "Notes");
        Assert.DoesNotContain(all, f => f.FullName == "INBOX/Notes");

        // A note filed from the client wears the Note mask, named by its subject, with the UUID field set.
        MimeKit.MimeMessage Note(string text)
        {
            var m = new MimeKit.MimeMessage();
            m.From.Add(new MimeKit.MailboxAddress("Me", email));
            m.Subject = "Shopping list";
            m.Headers.Add("X-Universally-Unique-Identifier", "note-uuid-1");
            m.Body = new MimeKit.TextPart("html") { Text = text };
            return m;
        }

        var notesFolder = await client.GetFolderAsync("Notes");
        var uid1 = (await notesFolder.AppendAsync(Note("<b>eggs</b>")))!.Value;

        var noteDocs = (await TestJson.Get(api, $"/api/documents/{notesId}/children")).GetProperty("children").EnumerateArray().ToList();
        var noteDoc = noteDocs.Single(c => c.GetProperty("name").GetString() == "Shopping list");
        Assert.Equal("Note", noteDoc.GetProperty("documentType").GetString());
        var noteId = noteDoc.GetProperty("id").GetGuid();
        var fields = (await TestJson.Get(api, $"/api/documents/{noteId}/index-data")).GetProperty("fields").EnumerateArray().ToList();
        Assert.Contains(fields, f => f.GetProperty("fieldName").GetString() == "Note UUID"
            && f.GetProperty("values").EnumerateArray().Any(v => v.GetString() == "note-uuid-1"));

        // The Apple edit dance: SELECT (session sees uid1), append the EDIT under the same UUID, then delete
        // + expunge the OLD message. The edit becomes a new VERSION of the same document; the expunge of the
        // superseded message is ABSORBED — the note survives with its history.
        await notesFolder.OpenAsync(FolderAccess.ReadWrite);
        var uid2 = (await notesFolder.AppendAsync(Note("<b>eggs and milk</b>")))!.Value;
        Assert.True(uid2.Id > uid1.Id);
        await notesFolder.AddFlagsAsync(uid1, MessageFlags.Deleted, silent: true);
        await notesFolder.ExpungeAsync();

        noteDocs = (await TestJson.Get(api, $"/api/documents/{notesId}/children")).GetProperty("children").EnumerateArray().ToList();
        var survived = noteDocs.Single(c => c.GetProperty("name").GetString() == "Shopping list");
        Assert.Equal(noteId, survived.GetProperty("id").GetGuid());   // SAME identity
        Assert.Equal(2, survived.GetProperty("versionCount").GetInt32()); // full history
        var bin = (await TestJson.Get(api, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(bin, i => i.GetProperty("name").GetString() == "Shopping list");

        await client.DisconnectAsync(true);
    }
    // The OTHER ordering of a notes edit: DELETE-old first, THEN append the replacement (#790).
    //
    // Only append-then-delete was survivable. In this order the delete really lands — currentUid equals the
    // message's uid, so the absorption guard cannot fire, and the note is soft-deleted like ordinary mail. The
    // re-append's correlation then could not see the soft-deleted row, so it forked a NEW document: measured
    // with Apple Notes as the note disappearing and returning as a stranger. The fix lets the correlation look
    // past the soft-delete and RESTORE the note — the append is the proof the client still has it, so restoring
    // completes the edit rather than second-guessing a deletion.
    [Fact]
    public async Task A_notes_edit_that_deletes_before_appending_keeps_the_same_note()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-note2-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "note-1234", "Note Editor");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "note-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var personalId = personal.GetProperty("id").GetGuid();
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox")
            .GetProperty("id").GetGuid();
        await TestJson.Post(api, $"/api/documents/{mailboxId}/children", new { name = "Notebook", folderMask = "notes" });
        var notesId = (await TestJson.Get(api, $"/api/documents/{mailboxId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Notebook").GetProperty("id").GetGuid();

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        MimeKit.MimeMessage Note(string text)
        {
            var m = new MimeKit.MimeMessage();
            m.From.Add(new MimeKit.MailboxAddress("Me", email));
            m.Subject = "Meeting notes";
            m.Headers.Add("X-Universally-Unique-Identifier", "note-uuid-2");
            m.Body = new MimeKit.TextPart("html") { Text = text };
            return m;
        }

        var notesFolder = await client.GetFolderAsync("Notes");
        var uid1 = (await notesFolder.AppendAsync(Note("<b>first draft</b>")))!.Value;
        var noteId = (await TestJson.Get(api, $"/api/documents/{notesId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Meeting notes").GetProperty("id").GetGuid();

        // DELETE first — the ordering the absorption guard cannot see coming — then append the edit.
        await notesFolder.OpenAsync(FolderAccess.ReadWrite);
        await notesFolder.AddFlagsAsync(uid1, MessageFlags.Deleted, silent: true);
        await notesFolder.ExpungeAsync();
        await notesFolder.AppendAsync(Note("<b>first draft, revised</b>"));

        // Same identity, full history — exactly what the other ordering already guaranteed.
        var children = (await TestJson.Get(api, $"/api/documents/{notesId}/children"))
            .GetProperty("children").EnumerateArray().ToList();
        var survived = Assert.Single(children, c => c.GetProperty("name").GetString() == "Meeting notes");
        Assert.Equal(noteId, survived.GetProperty("id").GetGuid());
        Assert.Equal(2, survived.GetProperty("versionCount").GetInt32());

        // …and it is NOT ALSO in the recycle bin: restored, not resurrected as a copy over a corpse.
        var bin = (await TestJson.Get(api, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(bin, i => i.GetProperty("name").GetString() == "Meeting notes");

        await client.DisconnectAsync(true);
    }

    // The SAME edit dance, inside a user-created SECTION (#812). The dispatch tested the Notebook mask alone,
    // so a note appended into a section took the mail path — whose Message-ID dedup cannot see an edit,
    // because a notes client regenerates the Message-ID on every edit (MimeKit stamps a fresh one per message
    // here for the same reason) and only the UUID header is stable. Measured on a phone as every edit becoming
    // a second note: creation "worked" (a message appeared), so the round that validated sections passed while
    // this defect shipped.
    [Fact]
    public async Task A_note_edited_in_a_created_section_versions_instead_of_duplicating()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-sect-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "sect-1234", "Section Writer");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "sect-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        // The notebook the way the sibling tests make one: under the mailbox the IMAP credential materialised.
        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox").GetProperty("id").GetGuid();
        await TestJson.Post(api, $"/api/documents/{mailboxId}/children", new { name = "Notebook", folderMask = "notes" });
        var notesId = (await TestJson.Get(api, $"/api/documents/{mailboxId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Notebook").GetProperty("id").GetGuid();

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        // The section the way Apple Notes makes one: CREATE "Notes/Travel" over the wire (#564).
        var notes = await client.GetFolderAsync("Notes");
        var section = (await notes.CreateAsync("Travel", true))!;
        Assert.NotNull(section);

        MimeKit.MimeMessage Note(string text)
        {
            var m = new MimeKit.MimeMessage();
            m.From.Add(new MimeKit.MailboxAddress("Me", email));
            m.Subject = "Packing list";
            m.Headers.Add("X-Universally-Unique-Identifier", "note-uuid-section-1");
            m.Body = new MimeKit.TextPart("html") { Text = text };
            return m;
        }

        var uid1 = (await section.AppendAsync(Note("<b>socks</b>")))!.Value;

        // The append landed as a NOTE, not as mail: the Note mask carries the correlation key the edit needs.
        var sectionId = (await TestJson.Get(api, $"/api/documents/{notesId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Travel").GetProperty("id").GetGuid();
        var created = (await TestJson.Get(api, $"/api/documents/{sectionId}/children"))
            .GetProperty("children").EnumerateArray().ToList();
        var noteDoc = Assert.Single(created, c => c.GetProperty("name").GetString() == "Packing list");
        Assert.Equal("Note", noteDoc.GetProperty("documentType").GetString());
        var noteId = noteDoc.GetProperty("id").GetGuid();

        // The Apple edit dance in the section: append the edit under the same UUID, delete + expunge the old.
        await section.OpenAsync(FolderAccess.ReadWrite);
        await section.AppendAsync(Note("<b>socks and a charger</b>"));
        await section.AddFlagsAsync(uid1, MessageFlags.Deleted, silent: true);
        await section.ExpungeAsync();

        // ONE note, SAME identity, TWO versions — not a sibling beside a corpse.
        var after = (await TestJson.Get(api, $"/api/documents/{sectionId}/children"))
            .GetProperty("children").EnumerateArray().ToList();
        var survived = Assert.Single(after, c => c.GetProperty("name").GetString() == "Packing list");
        Assert.Equal(noteId, survived.GetProperty("id").GetGuid());
        Assert.Equal(2, survived.GetProperty("versionCount").GetInt32());

        await client.DisconnectAsync(true);
    }

    // What LIST ADVERTISES, pinned on the raw socket (#792). Apple Notes never sent CREATE because nothing
    // told it creation was possible: the CHILDREN capability was absent (so \HasChildren had no contract) and
    // no attribute separated the one creatable subtree from the read-only rest. A capability the server holds
    // and never advertises produces the same silent loss as one it advertises and refuses — a healthy session
    // and a crippled one log identically, which is the SEARCH incident's shape (ADR 0626).
    //
    // Raw socket, not MailKit: the assertion is about exact tokens on the wire, and a tolerant client library
    // normalises away precisely what is being tested (the THREADID NIL lesson).
    [Fact]
    public async Task List_advertises_children_and_marks_only_the_notebook_tree_creatable()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-list-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "list-1234", "List Reader");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "list-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        // Provision the notebook so the Notes mailbox exists to be listed.
        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox").GetProperty("id").GetGuid();
        await TestJson.Post(api, $"/api/documents/{mailboxId}/children", new { name = "Notebook", folderMask = "notes" });

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

        await reader.ReadLineAsync(); // greeting

        async Task<List<string>> ExchangeAsync(string tag, string command)
        {
            await writer.WriteLineAsync($"{tag} {command}");
            var lines = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Add(line);
                if (line.StartsWith(tag + " ", StringComparison.Ordinal))
                {
                    break;
                }
            }

            return lines;
        }

        // CAPABILITY carries CHILDREN — the RFC 3348 contract for the attributes LIST is about to use.
        var capability = await ExchangeAsync("a1", "CAPABILITY");
        var capLine = Assert.Single(capability, l => l.StartsWith("* CAPABILITY", StringComparison.Ordinal));
        Assert.Contains(" CHILDREN", capLine, StringComparison.Ordinal);

        var authPlain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{email}\0{imapPassword}"));
        var login = await ExchangeAsync("a2", $"AUTHENTICATE PLAIN {authPlain}");
        Assert.Contains(login, l => l.StartsWith("a2 OK", StringComparison.Ordinal));

        var list = await ExchangeAsync("a3", "LIST \"\" \"*\"");

        // The notebook is the creatable place: never \Noinferiors, whatever its child state.
        var notes = Assert.Single(list, l => l.EndsWith(" \"Notes\"", StringComparison.Ordinal));
        Assert.DoesNotContain("\\Noinferiors", notes, StringComparison.Ordinal);

        // A read-only LEAF says \Noinferiors — creation there will be refused, so the affordance should never
        // be offered. Drafts is provisioned empty, which makes it the stable leaf to pin.
        var drafts = Assert.Single(list, l => l.EndsWith(" \"Drafts\"", StringComparison.Ordinal));
        Assert.Contains("\\Noinferiors", drafts, StringComparison.Ordinal);
        Assert.Contains("\\HasNoChildren", drafts, StringComparison.Ordinal);

        // …and never on a mailbox that HAS children: \Noinferiors claims no child can ever exist (RFC 3501
        // §7.2.2), which would be a lie told about the personal root.
        foreach (var line in list.Where(l => l.Contains("\\HasChildren", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("\\Noinferiors", line, StringComparison.Ordinal);
        }

        await ExchangeAsync("a4", "LOGOUT");
    }

    // The HTML half of #790: does a note's content type survive the round trip? The report showed Apple's own
    // markup (`overflow-wrap: break-word` is what Notes emits) DISPLAYED as text, which is a content-type
    // failure, not corruption. This pins the answer at the fetch: the body part comes back as text/html with
    // the text intact. If this holds, the display-as-text seen live was a consequence of the edit fork (the
    // ordering bug fixed above), not of the storage or fetch path.
    [Fact]
    public async Task An_html_note_round_trips_with_its_content_type()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-html-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "html-1234", "Html Writer");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "html-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox").GetProperty("id").GetGuid();
        await TestJson.Post(api, $"/api/documents/{mailboxId}/children", new { name = "Notebook", folderMask = "notes" });

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        // The body Apple Notes actually sends — its wrapper markup, stored verbatim.
        const string html = "<html><head></head><body style=\"overflow-wrap: break-word; -webkit-nbsp-mode: space;\">the note text</body></html>";
        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("Me", email));
        message.Subject = "Styled note";
        message.Headers.Add("X-Universally-Unique-Identifier", "note-uuid-html");
        message.Body = new MimeKit.TextPart("html") { Text = html };

        var notesFolder = await client.GetFolderAsync("Notes");
        var uid = (await notesFolder.AppendAsync(message))!.Value;

        await notesFolder.OpenAsync(FolderAccess.ReadOnly);
        var fetched = await notesFolder.GetMessageAsync(uid);
        var part = Assert.IsType<MimeKit.TextPart>(fetched.Body);
        Assert.True(part.ContentType.IsMimeType("text", "html"),
            $"the note came back as {part.ContentType.MimeType}; a client shown text/plain displays the markup as text");
        Assert.Equal(html, part.Text.TrimEnd('\r', '\n'));

        await client.DisconnectAsync(true);
    }
}
