using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// RFC 8474 OBJECTID (issue #780): the archive holds a stable id for every folder and every document, and until
// now exposed neither. What IMAP already had is not a substitute — a UID is scoped to one mailbox, and
// UIDVALIDITY is a cache-invalidation counter rather than a name.
//
// Driven through MailKit rather than raw sockets wherever MailKit understands the extension, because the point
// of a conforming id is that a REAL client can read it: `IMailFolder.Id` and `IMessageSummary.EmailId` are
// MailKit's own OBJECTID surface, and they stay null unless the server both advertises the capability and
// answers in the documented shape. A hand-parsed socket assertion would pass on output no client accepts.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ImapObjectIdTests
{
    private readonly E2EApiFactory _factory;

    public ImapObjectIdTests(E2EApiFactory factory) => _factory = factory;

    // The three claims that make the ids worth having, in one session because they are about ONE document seen
    // from two places:
    //   * two mailboxes have different MAILBOXIDs, and a RENAME preserves them (the fact a name cannot carry);
    //   * a document reached through its home folder and through a folder it is REFERENCED into reports the
    //     same EMAILID while carrying DIFFERENT UIDs — the exact gap the issue describes, and what RFC 8474 §4
    //     requires of a COPY (our COPY files a reference);
    //   * THREADID is nil, which the RFC permits and we owe honestly.
    [Fact]
    public async Task A_referenced_document_is_one_message_everywhere_and_a_rename_keeps_the_mailbox_id()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var homeName = $"ImapOidH{Guid.NewGuid():N}"[..12];
        var homeId = (await TestJson.Post(owner, "/api/repositories", new { name = homeName })).GetProperty("id").GetGuid();
        var viaName = $"ImapOidV{Guid.NewGuid():N}"[..12];
        var viaId = (await TestJson.Post(owner, "/api/repositories", new { name = viaName })).GetProperty("id").GetGuid();

        var email = $"imap-oid-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-oid-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Imap ObjectId User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent,
            (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        // Two children of `via` created FIRST, and the shared document created AFTER them. UIDs are handed out
        // in (CreatedAt, Id) order, so this makes the shared document the YOUNGEST in `via` (UID 3) while it is
        // the only message in `home` (UID 1). Created the other way round it is the oldest in both and draws
        // UID 1 twice — measured — and the assertion below would then pass on a coincidence rather than on the
        // two mailboxes numbering independently.
        await CreateDocumentAsync(owner, viaId, "via-child-a");
        await CreateDocumentAsync(owner, viaId, "via-child-b");

        // One document, two appearances: it LIVES in `home` and is REFERENCED into `via`.
        var docId = await CreateDocumentAsync(owner, homeId, "one-document");
        await TestJson.Post(owner, $"/api/documents/{viaId}/references", new { targetId = docId });

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

        string homeMailboxId, viaMailboxId, homeEmailId, viaEmailId;
        uint homeUid, viaUid;
        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await client.AuthenticateAsync(email, imapPassword);

            // Without the advertised capability MailKit does not ASK for the ids, so every assertion below
            // would read null and the test would be measuring its own silence.
            Assert.True(client.Capabilities.HasFlag(ImapCapabilities.ObjectID),
                $"OBJECTID not advertised; capabilities were {client.Capabilities}");

            var home = await client.GetFolderAsync(homeName);
            await home.OpenAsync(FolderAccess.ReadOnly);
            var homeSummary = Assert.Single(
                await home.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.EmailId | MessageSummaryItems.ThreadId));

            var via = await client.GetFolderAsync(viaName);
            await via.OpenAsync(FolderAccess.ReadOnly);
            var viaSummary = Assert.Single(
                await via.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.EmailId | MessageSummaryItems.Envelope | MessageSummaryItems.ThreadId),
                s => s.Envelope!.Subject == "one-document.txt");

            homeMailboxId = home.Id!;
            viaMailboxId = via.Id!;
            homeEmailId = homeSummary.EmailId!;
            viaEmailId = viaSummary.EmailId!;
            homeUid = homeSummary.UniqueId.Id;
            viaUid = viaSummary.UniqueId.Id;

            // RFC 8474 §6: a server that cannot calculate relationships MUST return NIL. MailKit surfaces that
            // as null — and asserting it explicitly is what stops a later "improvement" from inventing threads.
            Assert.Null(homeSummary.ThreadId);

            await client.DisconnectAsync(true);
        }

        Assert.False(string.IsNullOrEmpty(homeMailboxId), "the home mailbox reported no MAILBOXID");
        Assert.False(string.IsNullOrEmpty(viaMailboxId), "the referencing mailbox reported no MAILBOXID");
        Assert.NotEqual(homeMailboxId, viaMailboxId);

        // The heart of it. The two UIDs differ — that is correct and unavoidable, a UID is per-mailbox — and it
        // is precisely why the EMAILID matters: without it nothing on the wire says these are one document.
        Assert.NotEqual(homeUid, viaUid);
        Assert.Equal(homeEmailId, viaEmailId);
        Assert.Equal(docId.ToString("N"), homeEmailId);

        // A RENAME is the case a name-derived id cannot survive, and RFC 8474 §5 requires it to: rename the
        // repository in the workbench, and the mailbox a client tracked must still be the same mailbox.
        var renamed = $"{homeName}R";
        await RenameAsync(owner, homeId, renamed);

        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await client.AuthenticateAsync(email, imapPassword);

            Assert.Null(await SafeGetFolderAsync(client, homeName));
            var moved = await client.GetFolderAsync(renamed);
            await moved.OpenAsync(FolderAccess.ReadOnly);

            Assert.Equal(homeMailboxId, moved.Id);
            await client.DisconnectAsync(true);
        }

        // STATUS carries it too (RFC 8474 §5) — how a client checks identity during a LIST sweep WITHOUT
        // selecting every mailbox. MailKit has no API for a STATUS item it does not model, so this one is read
        // off the wire; it is also the only place the "echo only what was asked for" rule is visible.
        await AssertStatusCarriesMailboxIdAsync(port, email, imapPassword, renamed, homeMailboxId);
    }

    // Re-filing a message a client already sent us. The Notes path has correlated on a client UUID since #562
    // slice 5; e-mail had nothing, so a resync, a second drag or a rule that fires twice silently multiplied
    // documents. Correlation is on the eMail mask's existing "Entry ID" field — no new storage (#780).
    [Fact]
    public async Task Re_appending_the_same_message_adds_a_version_instead_of_a_second_document()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"ImapDup{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"imap-dup-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-dup-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Imap Dedup User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent,
            (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        var messageId = $"dedupe-{Guid.NewGuid():N}@e2e.local";
        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);
        var repo = await client.GetFolderAsync(repoName);

        // The SAME message twice, exactly as a client that lost its state and re-uploaded would send it. The
        // second body differs by a word so a passing test cannot be explained by "the bytes were identical and
        // something upstream de-duplicated them" — correlation must be on the Message-ID, not on a hash.
        var first = await repo.AppendAsync(Message(messageId, "the first send"));
        var second = await repo.AppendAsync(Message(messageId, "the very same message, re-sent"));

        await repo.OpenAsync(FolderAccess.ReadOnly);
        var summaries = await repo.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.EmailId);
        await client.DisconnectAsync(true);

        // One message in the mailbox, not two.
        var only = Assert.Single(summaries);

        // …and the UID did not move. This is where e-mail deliberately parts from the Notes path, which bumps
        // the UID because an edit there is append-then-DELETE. An .eml re-append has no paired delete, so a new
        // UID would make the message vanish and reappear in every connected client to say nothing had changed.
        Assert.Equal(first, second);
        Assert.Equal(first!.Value.Id, only.UniqueId.Id);

        // The second append was not discarded — it is a new VERSION of the one document, which is the whole
        // point of correlating rather than refusing. Asserting the count is what separates "deduplicated" from
        // "silently dropped the second copy".
        var docId = Guid.ParseExact(only.EmailId!, "N");
        var versions = await TestJson.Get(api, $"/api/documents/{docId}/versions");
        Assert.Equal(2, versions.GetProperty("versions").GetArrayLength());

        // A different Message-ID in the same folder is a different document — the guard against a correlation
        // so eager it swallows unrelated mail.
        using var second_client = new ImapClient();
        await second_client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await second_client.AuthenticateAsync(email, imapPassword);
        var again = await second_client.GetFolderAsync(repoName);
        await again.AppendAsync(Message($"other-{Guid.NewGuid():N}@e2e.local", "an unrelated message"));
        await again.OpenAsync(FolderAccess.ReadOnly);
        var after = await again.FetchAsync(0, -1, MessageSummaryItems.EmailId);
        await second_client.DisconnectAsync(true);

        Assert.Equal(2, after.Count);
        Assert.Equal(2, after.Select(s => s.EmailId).Distinct().Count());
        _ = userId;
    }

    private static MimeMessage Message(string messageId, string body)
    {
        var mime = new MimeMessage
        {
            Subject = "A message that gets sent twice",
            MessageId = messageId,
            Date = DateTimeOffset.UtcNow,
            Body = new TextPart("plain") { Text = body },
        };
        mime.From.Add(new MailboxAddress("Sender", "sender@e2e.local"));
        mime.To.Add(new MailboxAddress("Recipient", "recipient@e2e.local"));
        return mime;
    }

    private static async Task<IMailFolder?> SafeGetFolderAsync(ImapClient client, string name)
    {
        try
        {
            return await client.GetFolderAsync(name);
        }
        catch (FolderNotFoundException)
        {
            return null;
        }
    }

    private static async Task RenameAsync(HttpClient http, Guid documentId, string name)
    {
        var get = await http.GetAsync($"/api/documents/{documentId}");
        get.EnsureSuccessStatusCode();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}")
        {
            Content = JsonContent.Create(new { name }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.Tag);
        (await http.SendAsync(put)).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateDocumentAsync(HttpClient http, Guid parentId, string name)
    {
        var docId = (await TestJson.Post(http, $"/api/documents/{parentId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(http, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.ASCII.GetBytes("content")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        return docId;
    }

    // STATUS (mailbox) (MAILBOXID) — read raw, since MailKit models no such status item.
    private static async Task AssertStatusCarriesMailboxIdAsync(int port, string email, string password, string mailbox, string expected)
    {
        using var raw = new TcpClient();
        await raw.ConnectAsync("127.0.0.1", port);
        await using var stream = raw.GetStream();
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        async Task<string> ReadUntilAsync(string tag)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (!Encoding.Latin1.GetString(buffer.ToArray()).Contains($"\r\n{tag} ", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(chunk).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.Latin1.GetString(buffer.ToArray());
        }

        await writer.WriteLineAsync($"a1 LOGIN {email} {password}");
        await ReadUntilAsync("a1");
        await writer.WriteLineAsync($"a2 STATUS \"{mailbox}\" (MESSAGES MAILBOXID)");
        var asked = await ReadUntilAsync("a2");
        Assert.Contains($"MAILBOXID ({expected})", asked, StringComparison.Ordinal);

        // …and NOT volunteered to a client that did not ask, per RFC 3501 §6.3.10. A client predating OBJECTID
        // would not know what to do with an item it never requested.
        await writer.WriteLineAsync($"a3 STATUS \"{mailbox}\" (MESSAGES)");
        var unasked = await ReadUntilAsync("a3");
        Assert.DoesNotContain("MAILBOXID", unasked, StringComparison.Ordinal);

        // The FETCH items on the wire, in the CASE they are actually written. No client library can pin this:
        // MailKit maps both `NIL` and `nil` to null, so the assertion in the test above passes either way —
        // and every other NIL this server emits is uppercase, which is the IMAP convention a stricter parser
        // is entitled to rely on. Caught only by reading the bytes.
        await writer.WriteLineAsync($"a4 SELECT \"{mailbox}\"");
        await ReadUntilAsync("a4");
        await writer.WriteLineAsync("a5 FETCH 1 (EMAILID THREADID)");
        var fetched = await ReadUntilAsync("a5");
        Assert.Contains("THREADID NIL", fetched, StringComparison.Ordinal);
        // …and EMAILID is present in the shape RFC 8474 §3 mandates: parenthesised, and drawn only from
        // a-z A-Z 0-9 _ - (a GUID in "N" form is 32 lowercase hex, well inside that).
        Assert.Matches(@"EMAILID \([0-9a-f]{32}\)", fetched);
    }
}
