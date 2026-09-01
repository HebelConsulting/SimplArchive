using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MailKit.Net.Imap;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// The IMAP endpoint, slice 1 (#562, ADR "IMAP endpoint (read-only, first slice)"), driven end to end with a
// REAL mail-client library (MailKit) against the hosted TcpListener on its ephemeral test port: generated
// IMAP password auth, the mailbox tree (INBOX = personal repo root, shared repositories, no Intray/Check-out),
// native .eml serving, the ShowAllDocuments toggle's synthetic attachment messages, stable UIDs across
// reconnects, and the read-only refusals.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ImapEndpointTests
{
    private readonly E2EApiFactory _factory;

    public ImapEndpointTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Imap_serves_the_acl_true_mailbox_tree_with_generated_credentials()
    {
        // ---- Arrange: a repository holding one email + one PDF, and a user who can see it -------------
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Imap{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"imap-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Imap User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // The app-specific IMAP password, via the me resource's advertised surface.
        var status = await TestJson.Get(api, "/api/me/imap-access");
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.False(status.GetProperty("enabled").GetBoolean());

        // The port a user is told to dial is the PUBLISHED one, never the bound one (#682). Decisive here
        // because the fixture binds EPHEMERALLY: a regression could not coincidentally report 143, it would
        // report whatever random port the listener got. The kiosk shipped the other way round — bound 9993,
        // published 993, advertised 9993 — and sent every user to a port nothing outside can open.
        var bound = ((SimplArchive.Api.Imap.ImapServer)_factory.Services.GetService(typeof(SimplArchive.Api.Imap.ImapServer))!).BoundPort!.Value;
        Assert.Equal(143, status.GetProperty("port").GetInt32());
        Assert.NotEqual(bound, status.GetProperty("port").GetInt32());
        var generated = await TestJson.Post(api, "/api/me/imap-access", new { });
        var imapPassword = generated.GetProperty("password").GetString()!;
        Assert.Equal(email, generated.GetProperty("username").GetString());

        // File an .eml and a .pdf into the repository over WebDAV (same credential family, easiest byte path).
        var davGen = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var basic = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davGen.GetProperty("password").GetString()}")));
        using var dav = _factory.CreateClient();
        var eml = Encoding.ASCII.GetBytes(
            "From: Alice <alice@example.test>\r\nTo: Bob <bob@example.test>\r\nSubject: Quarterly numbers\r\n"
            + "Message-ID: <q1@example.test>\r\nDate: Mon, 06 Jul 2026 10:00:00 +0000\r\n\r\nThe numbers look fine.\r\n");
        foreach (var (name, bytes) in new[] { ("report.eml", eml), ("summary.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 fake")) })
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/{repoName}/{name}")
            {
                Content = new ByteArrayContent(bytes),
                Headers = { Authorization = basic },
            };
            Assert.Equal(System.Net.HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);
        }

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

        // ---- The mailbox tree + native .eml serving ---------------------------------------------------
        int uidBefore;
        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await client.AuthenticateAsync(email, imapPassword);

            var personal = client.PersonalNamespaces[0];
            var all = await client.GetFoldersAsync(personal);
            Assert.Contains(all, f => f.FullName == "INBOX");
            Assert.Contains(all, f => f.FullName == repoName);
            Assert.DoesNotContain(all, f => f.FullName.Contains("Intray") || f.FullName.Contains("Check-out"));

            // Default view: emails only — the PDF stays invisible until the toggle flips (#562).
            var repo = await client.GetFolderAsync(repoName);
            await repo.OpenAsync(FolderAccess.ReadOnly);
            Assert.Equal(1, repo.Count);
            var message = await repo.GetMessageAsync(0);
            Assert.Equal("Quarterly numbers", message.Subject);
            Assert.Contains("The numbers look fine.", message.TextBody);

            var summary = (await repo.FetchAsync(new[] { 0 }, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope)).Single();
            uidBefore = (int)summary.UniqueId.Id;
            Assert.Equal("Quarterly numbers", summary.Envelope!.Subject);

            // The structure is the archive's: creating a mailbox is refused.
            var inbox = await client.GetFolderAsync("INBOX");
            await Assert.ThrowsAnyAsync<Exception>(() => inbox.CreateAsync("new-folder", true));
            await client.DisconnectAsync(true);
        }

        // ---- The toggle: every visible document, non-emails as synthetic attachment messages ----------
        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await client.AuthenticateAsync(email, imapPassword);
            var repo = await client.GetFolderAsync(repoName);
            await repo.OpenAsync(FolderAccess.ReadOnly);
            Assert.Equal(2, repo.Count);

            var summaries = await repo.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope);
            // The .eml keeps its UID across sessions — clients cache by it (RFC 3501).
            Assert.Contains(summaries, s => (int)s.UniqueId.Id == uidBefore && s.Envelope!.Subject == "Quarterly numbers");

            var synthetic = summaries.Single(s => s.Envelope!.Subject == "summary.pdf");
            var pdfMessage = await repo.GetMessageAsync(synthetic.UniqueId);
            var attachment = Assert.Single(pdfMessage.Attachments);
            Assert.Equal("summary.pdf", attachment.ContentDisposition!.FileName);

            // The body line links to the product (#783) — a bare URL, so clients auto-link it and the part
            // tree stays untouched: the attachment must remain part 2, because clients address it as BODY[2]
            // and moving it is the #766 defect class. Both facts asserted together, so the link can never be
            // "improved" into an HTML sibling without this test asking about the section number.
            Assert.Contains("https://www.simplarchive.dev", pdfMessage.TextBody);
            Assert.Single(pdfMessage.Attachments);
            await client.DisconnectAsync(true);
        }
    }

    [Fact]
    public async Task Imap_refuses_wrong_and_revoked_credentials()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-no-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "imap-no-1234", "No Imap");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "imap-no-1234"));

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

        // No generated password yet → the login password does NOT work (the credential is separate by design).
        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => client.AuthenticateAsync(email, "imap-no-1234"));
        }

        // Generated, then revoked → refused again.
        var generated = await TestJson.Post(api, "/api/me/imap-access", new { });
        var imapPassword = generated.GetProperty("password").GetString()!;
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await api.DeleteAsync("/api/me/imap-access")).StatusCode);

        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => client.AuthenticateAsync(email, imapPassword));
        }
    }

    [Fact]
    public async Task Seen_state_persists_per_user_across_reconnects()
    {
        // Arrange: a repo with one email, a user with IMAP credentials.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Seen{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"imap-seen-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "seen-1234", "Seen User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "seen-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        var davPw = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPw}")));
        using var dav = _factory.CreateClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/{repoName}/note.eml")
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("From: a@b.test\r\nSubject: Mark me\r\n\r\nbody\r\n")),
            Headers = { Authorization = basic },
        };
        Assert.Equal(System.Net.HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

        // Fresh message: unseen; the mailbox opens READ-WRITE and \Seen is a permanent flag.
        MailKit.UniqueId uid;
        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await client.AuthenticateAsync(email, imapPassword);
            var repo = await client.GetFolderAsync(repoName);
            Assert.Equal(FolderAccess.ReadWrite, await repo.OpenAsync(FolderAccess.ReadWrite));

            var summary = (await repo.FetchAsync(new[] { 0 }, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags)).Single();
            Assert.False(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
            uid = summary.UniqueId;

            await repo.AddFlagsAsync(uid, MessageFlags.Seen, silent: true);
            await client.DisconnectAsync(true);
        }

        // The mark survives the reconnect — it is a row, not session memory (#562 slice 2).
        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await client.AuthenticateAsync(email, imapPassword);
            var repo = await client.GetFolderAsync(repoName);
            await repo.OpenAsync(FolderAccess.ReadWrite);
            var summary = (await repo.FetchAsync(new[] { uid }, MessageSummaryItems.Flags)).Single();
            Assert.True(summary.Flags!.Value.HasFlag(MessageFlags.Seen));

            // STATUS reports a real unseen count: 0 now, 1 again after removing the flag.
            Assert.Equal(0, (await client.GetFolderAsync(repoName)).Unread);
            await repo.RemoveFlagsAsync(uid, MessageFlags.Seen, silent: true);
            var status = await client.GetFolderAsync(repoName);
            await status.StatusAsync(StatusItems.Unread);
            Assert.Equal(1, status.Unread);
            await client.DisconnectAsync(true);
        }
    }

    [Fact]
    public async Task Append_expunge_move_and_copy_map_to_archive_semantics()
    {
        // Arrange: a repo with a subfolder, a user with IMAP credentials.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Wr{Guid.NewGuid():N}"[..10];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"imap-wr-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "wr-1234", "Writer", canViewAuditLog: true);
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "wr-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "Archive" });

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        // ---- APPEND files the .eml through the shared finalizer (name = Subject, eMail mask) ----------
        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("Alice", "alice@example.test"));
        message.To.Add(new MimeKit.MailboxAddress("Bob", "bob@example.test"));
        message.Subject = "Filed from the mail client";
        message.Body = new MimeKit.TextPart("plain") { Text = "archived over IMAP" };

        var repo = await client.GetFolderAsync(repoName);
        var appended = await repo.AppendAsync(message);
        Assert.NotNull(appended); // APPENDUID came back

        var children = (await TestJson.Get(owner, $"/api/documents/{repoId}/children")).GetProperty("children").EnumerateArray().ToList();
        var filed = children.Single(c => c.GetProperty("name").GetString() == "Filed from the mail client");
        Assert.Equal("eMail", filed.GetProperty("documentType").GetString()); // the finalizer classified it

        // A second identical Subject auto-suffixes instead of clashing. Both messages carry the SAME
        // envelope subject — the archive names differ — so the APPENDUIDs are what tells them apart.
        //
        // It must be a DIFFERENT message, not the same object appended twice. MimeKit auto-generates one
        // Message-ID per MimeMessage, so re-appending the same object sends the same Message-ID — which the
        // .eml correlation (#780) reads as a re-filing and answers with a new VERSION rather than a sibling.
        // That is the decided behaviour, so exercising the NAME-clash path needs two genuinely distinct mails
        // that happen to share a subject, which is also the only shape a user ever actually produces.
        var other = new MimeKit.MimeMessage { Subject = message.Subject, MessageId = $"other-{Guid.NewGuid():N}@example.test" };
        other.From.Add(new MimeKit.MailboxAddress("Carol", "carol@example.test"));
        other.To.Add(new MimeKit.MailboxAddress("Bob", "bob@example.test"));
        other.Body = new MimeKit.TextPart("plain") { Text = "a different mail that happens to share a subject" };

        var appendedSecond = await repo.AppendAsync(other);
        children = (await TestJson.Get(owner, $"/api/documents/{repoId}/children")).GetProperty("children").EnumerateArray().ToList();
        Assert.Contains(children, c => c.GetProperty("name").GetString() == "Filed from the mail client (2)");

        // ---- MOVE reparents; COPY files a reference ---------------------------------------------------
        await repo.OpenAsync(FolderAccess.ReadWrite);
        var archive = await client.GetFolderAsync($"{repoName}/Archive");
        var first = appended!.Value;
        var second = appendedSecond!.Value;

        await repo.MoveToAsync(first, archive);
        var archiveId = children.Single(c => c.GetProperty("name").GetString() == "Archive").GetProperty("id").GetGuid();
        var archiveChildren = (await TestJson.Get(owner, $"/api/documents/{archiveId}/children")).GetProperty("children").EnumerateArray().ToList();
        Assert.Contains(archiveChildren, c => c.GetProperty("name").GetString() == "Filed from the mail client");

        await repo.CopyToAsync(second, archive);
        var references = (await TestJson.Get(owner, $"/api/documents/{archiveId}/references")).GetProperty("references").EnumerateArray().ToList();
        Assert.Contains(references, r => r.GetProperty("name").GetString() == "Filed from the mail client (2)");

        // ---- \Deleted + EXPUNGE = soft delete (recycle bin), not a purge ------------------------------
        await repo.AddFlagsAsync(second, MessageFlags.Deleted, silent: true);
        await repo.ExpungeAsync();
        children = (await TestJson.Get(owner, $"/api/documents/{repoId}/children")).GetProperty("children").EnumerateArray().ToList();
        Assert.DoesNotContain(children, c => c.GetProperty("name").GetString() == "Filed from the mail client (2)");
        var bin = (await TestJson.Get(api, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(bin, i => i.GetProperty("name").GetString() == "Filed from the mail client (2)");

        await client.DisconnectAsync(true);

        // Every IMAP mutation is audited with its workbench twin's action (#562 slice 4, ADR 0597).
        var audit = (await TestJson.Get(api, "/api/audit-events?limit=200")).GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "Document.Filed" && e.GetProperty("details").GetString() == "Filed over IMAP");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "Document.Moved" && e.GetProperty("details").GetString() == "Moved over IMAP");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "Reference.Added" && e.GetProperty("details").GetString() == "Referenced over IMAP (COPY)");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "Document.Deleted" && e.GetProperty("details").GetString() == "Deleted over IMAP (EXPUNGE)");
    }


    // The eMail-Archive (#802): the user-organizable half of the mailbox, driven over IMAP the way a mail
    // client drives it. One test for the whole lifecycle because the steps only mean anything in sequence —
    // a rename of a folder that was never listed, or a Trash move out of a folder never filed into, would
    // each pass vacuously alone.
    [Fact]
    public async Task The_email_archive_supports_folders_over_imap_end_to_end()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-arch-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "arch-1234", "Archive User");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "arch-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        // Provisioning: the sixth standing folder exists in the workbench under My Mailbox…
        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox").GetProperty("id").GetGuid();
        var archive = (await TestJson.Get(api, $"/api/documents/{mailboxId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "eMail-Archive");
        var archiveId = archive.GetProperty("id").GetGuid();

        // …and the workbench menu offers exactly the mail folder there: the admits list rides each listing
        // row as mask-data both clients read, so this one assertion covers the affordance in both (ADR 0656).
        var admits = archive.GetProperty("admits").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("IMAP Folder", admits);
        Assert.DoesNotContain("Folder", admits);

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        // The wire projection: root-level "Archive", the RFC 6154 attribute a client's archive button keys on.
        var personalNs = client.PersonalNamespaces[0];
        var archiveFolder = await client.GetFolderAsync("Archive");
        Assert.True(archiveFolder.Attributes.HasFlag(FolderAttributes.Archive),
            "the Archive mailbox must advertise \\Archive, or every client invents its own beside it");

        // CREATE — the verb the whole feature exists for.
        var work = (await archiveFolder.CreateAsync("Work", isMessageFolder: true))!;
        Assert.Equal("Archive/Work", work.FullName);

        // A message filed into the user folder…
        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("Someone", "someone@example.com"));
        message.To.Add(new MimeKit.MailboxAddress("Me", email));
        message.Subject = "Quarterly numbers";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        message.Body = new MimeKit.TextPart("plain") { Text = "the figures" };
        var uid = (await work.AppendAsync(message))!.Value;

        // …deletes with MAIL semantics: to Trash, not to the recycle bin — the folder the user made must not
        // change what deletion means (the ephemeral-tier walk, nested one level below a standing folder).
        await work.OpenAsync(FolderAccess.ReadWrite);
        await work.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true);
        await work.ExpungeAsync();
        var trash = await client.GetFolderAsync("Trash");
        await trash.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(1, trash.Count);

        // RENAME renames the leaf…
        await work.RenameAsync(archiveFolder, "Projects");
        var listed = (await client.GetFoldersAsync(personalNs)).Select(f => f.FullName).ToList();
        Assert.Contains("Archive/Projects", listed);
        Assert.DoesNotContain("Archive/Work", listed);

        // …and DELETE soft-deletes into the recycle bin, where the workbench can bring it back.
        var projects = await client.GetFolderAsync("Archive/Projects");
        await projects.DeleteAsync();
        Assert.DoesNotContain("Archive/Projects",
            (await client.GetFoldersAsync(personalNs)).Select(f => f.FullName));
        var bin = (await TestJson.Get(api, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(bin, i => i.GetProperty("name").GetString() == "Projects");

        // The provisioned tree stays read-only: the standing refusal, not a new hole.
        var inbox = client.Inbox;
        await Assert.ThrowsAnyAsync<Exception>(() => inbox.RenameAsync(inbox.ParentFolder!, "Postbox"));

        await client.DisconnectAsync(true);
    }

    // The heal half of provisioning (#802): a personal space that PREDATES the sixth folder gains it on the
    // next provisioning pass. The trap this pins is the grow-later seed that only fresh volumes ever see —
    // every tenant a test creates is new, so without forcing the old state the heal path never runs (#664).
    [Fact]
    public async Task An_existing_mailbox_gains_the_email_archive_on_the_next_provisioning_pass()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-heal-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "heal-1234", "Heal User");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "heal-1234"));

        // The mailbox provisioning hangs off the IMAP credential, not off the personal space alone.
        await TestJson.Post(api, "/api/me/imap-access", new { });
        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox").GetProperty("id").GetGuid();

        // Force the pre-#802 state: the folder ceases to exist, as it never existed for a space provisioned
        // before the release. Hard-deleted rather than soft: a soft-deleted folder would test the restore
        // path, not the provisioning one.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            var archiveDoc = await db.Documents.IgnoreQueryFilters(["TenantFilter"])
                .SingleAsync(d => d.ParentId == mailboxId && d.Name == "eMail-Archive");
            db.Documents.Remove(archiveDoc);
            await db.SaveChangesAsync();
        }

        // The next pass is a plain IMAP LOGIN — the path the live report exposed (#802): a user whose
        // credential predates the new folder rotates nothing and receives no delivery, so login is the ONLY
        // provisioning moment their account ever reaches. Healing on rotation alone strands exactly them.
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            var again = await db.Documents.IgnoreQueryFilters(["TenantFilter"])
                .SingleAsync(d => d.ParentId == mailboxId && d.Name == "eMail-Archive");
            db.Documents.Remove(again);
            await db.SaveChangesAsync();
        }

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await client.AuthenticateAsync(email, imapPassword);
            Assert.Contains(await client.GetFoldersAsync(client.PersonalNamespaces[0]), f => f.FullName == "Archive");
            await client.DisconnectAsync(true);
        }

        var healed = (await TestJson.Get(api, $"/api/documents/{mailboxId}/children"))
            .GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("eMail-Archive", healed);
    }

    // A synthetic message serves its sections honestly, on a 7-bit CRLF wire (#802's live find).
    //
    // Measured with a real client on a demo document whose name carries an em-dash: Apple Mail asked
    // `BODY.PEEK[TEXT]<0.16384>` and was answered with the WHOLE message, unlabeled — 41843 bytes of headers,
    // boundaries and base64 rendered as the message text. Three defects in one line, all pinned here on the
    // raw socket because each is about exact bytes: the synthetic serializer wrote LF-only newlines (so our
    // own CRLFCRLF header scan failed and TEXT degraded to everything), the <start.count> partial was silently
    // dropped, and the decoded em-dash went bare into a quoted string on a 7-bit protocol.
    [Fact]
    public async Task A_synthetic_message_slices_its_text_honours_partials_and_stays_seven_bit()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var email = $"imap-wire-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "wire-1234", "Wire Reader");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "wire-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        // Documents, not just emails, over IMAP — a PDF only becomes a synthetic message with the per-user
        // show-all preference on (#793).
        (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).EnsureSuccessStatusCode();

        // A PDF document with a non-ASCII name in the user's own archive folder — the synthetic-message case.
        await TestJson.Post(api, "/api/me/personal-repository", new { });
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Wire{Guid.NewGuid():N}"[..10] }))
            .GetProperty("id").GetGuid();
        var stem = "Invoice — January";
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = stem })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".pdf" });
        using (var storage = new HttpClient())
        {
            var bytes = new byte[4000];
            Array.Fill(bytes, (byte)0x41);
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.Latin1);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
        await reader.ReadLineAsync();

        async Task<string> ExchangeAsync(string tag, string command)
        {
            await writer.WriteLineAsync($"{tag} {command}");
            var sb = new StringBuilder();
            var buffer = new char[65536];
            while (true)
            {
                var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                sb.Append(buffer, 0, read);
                if (sb.ToString().Contains($"\r\n{tag} ", StringComparison.Ordinal) || sb.ToString().StartsWith($"{tag} ", StringComparison.Ordinal))
                {
                    break;
                }
            }

            return sb.ToString();
        }

        var authPlain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{email}\0{imapPassword}"));
        Assert.Contains("a1 OK", await ExchangeAsync("a1", $"AUTHENTICATE PLAIN {authPlain}"));

        var repoName = (await TestJson.Get(api, $"/api/documents/{repoId}")).GetProperty("name").GetString();
        Assert.Contains("a2 OK", await ExchangeAsync("a2", $"SELECT \"{repoName}\""));

        // 1. The whole exchange is 7-bit: the em-dash travels as an RFC 2047 word, never as raw UTF-8.
        var structure = await ExchangeAsync("a3", "UID FETCH 1 (BODYSTRUCTURE ENVELOPE)");
        Assert.DoesNotContain('—', structure);
        Assert.Contains("=?utf-8?", structure, StringComparison.OrdinalIgnoreCase);

        // 2. TEXT is the body section, not the message: strictly smaller than BODY[], and it must not carry
        //    the top-level headers the client already fetched separately.
        var whole = await ExchangeAsync("a4", "UID FETCH 1 BODY.PEEK[]");
        var text = await ExchangeAsync("a5", "UID FETCH 1 BODY.PEEK[TEXT]");
        int LiteralSize(string response) => int.Parse(
            System.Text.RegularExpressions.Regex.Match(response, @"\{(\d+)\}").Groups[1].Value);
        Assert.True(LiteralSize(text) < LiteralSize(whole),
            $"BODY[TEXT] ({LiteralSize(text)}) must be smaller than BODY[] ({LiteralSize(whole)}) — equal means the header scan failed and the whole message was served as text");
        Assert.DoesNotContain("Message-Id:", text.Split("\r\n\r\n")[0][text.IndexOf('{')..], StringComparison.OrdinalIgnoreCase);

        // 3. A partial is sliced AND labeled with its origin octet — an unlabeled full answer to a ranged ask
        //    is what a client splices at the wrong offset or renders whole.
        var partial = await ExchangeAsync("a6", "UID FETCH 1 BODY.PEEK[TEXT]<0.64>");
        Assert.Contains("BODY[TEXT]<0> {64}", partial, StringComparison.Ordinal);

        // …and the CRLF discipline that makes the TEXT slice work is asserted at its root: the serialized
        // message uses CRLF newlines, so the header/body boundary exists on the wire.
        Assert.Contains("\r\n\r\n", whole[whole.IndexOf('{')..], StringComparison.Ordinal);

        await ExchangeAsync("a7", "LOGOUT");
    }

    // Organizing mail WITHIN the tier leaves no shortcut behind (#802, live find).
    //
    // A COPY out of a staging folder files-for-real and leaves a reference where the message was — the right
    // behaviour for filing into the REPOSITORY, where the mailbox keeping a pointer is the feature. Dragging
    // Inbox → Archive/Work is the other thing: organizing, whose whole promise is that the mail LEAVES Inbox.
    // Measured live: the drag left a reference in Inbox, the opposite of the promise.
    //
    // The sequence is the client's real move emulation — COPY, then STORE \Deleted + EXPUNGE on the source
    // entry — so this also pins the second half: the expunge aims at a stale entry whose document has already
    // moved, and it must absorb rather than yank the freshly organized mail into Trash.
    [Fact]
    public async Task Organizing_mail_into_an_archive_folder_moves_it_whole()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-org-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "org-1234", "Organizer");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "org-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var archiveRoot = await client.GetFolderAsync("Archive");
        var work = (await archiveRoot.CreateAsync("Sorted", isMessageFolder: true))!;

        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("Someone", "someone@example.com"));
        message.Subject = "To be sorted";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        message.Body = new MimeKit.TextPart("plain") { Text = "sort me" };

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var uid = (await inbox.AppendAsync(message))!.Value;

        // Re-select so the session's snapshot carries the appended message — a real client refreshes on the
        // EXISTS it is sent; a UID COPY against a stale snapshot silently matches nothing.
        await inbox.CloseAsync();
        await inbox.OpenAsync(FolderAccess.ReadWrite);

        // The measured sequence: COPY, then the move emulation's delete of the source entry.
        await inbox.CopyToAsync(uid, work);
        await inbox.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true);
        await inbox.ExpungeAsync();

        // The mail lives in the archive folder and ONLY there: no shortcut in Inbox, and the expunge did not
        // pull it into Trash.
        await work.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(1, work.Count);
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(0, inbox.Count);
        var trash = await client.GetFolderAsync("Trash");
        await trash.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(0, trash.Count);

        // Said against the store as well, where a reference and a listing row are distinguishable: the tier
        // holds no reference rows for this mail anywhere.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            Assert.False(await db.DocumentReferences.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(r => r.TenantId == tenantId));
        }

        await client.DisconnectAsync(true);
    }

    // Acting on a SHORTCUT acts on the shortcut (#802, second live find + refinement).
    //
    // Filing Inbox → repository leaves a reference in Inbox by design. That reference projects into the
    // mailbox as an ordinary-looking message, and nothing marked it — so moving it re-parented the TARGET
    // document out of the repository, and deleting it (after the expunge guard) silently kept it. The rule is
    // WebDAV's #769 rule on this surface: the appearance moves, copies and deletes as the appearance, and the
    // document never learns it happened.
    [Fact]
    public async Task A_filed_shortcut_moves_and_deletes_as_the_shortcut()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var email = $"imap-ref-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "ref-1234", "Ref Mover");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "ref-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        await TestJson.Post(api, "/api/me/personal-repository", new { });
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Ref{Guid.NewGuid():N}"[..10] }))
            .GetProperty("id").GetGuid();
        var repoName = (await TestJson.Get(api, $"/api/documents/{repoId}")).GetProperty("name").GetString();

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var archiveRoot = await client.GetFolderAsync("Archive");
        var sorted = (await archiveRoot.CreateAsync("Shortcuts", isMessageFolder: true))!;

        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("Someone", "someone@example.com"));
        message.Subject = "Contract scan";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        message.Body = new MimeKit.TextPart("plain") { Text = "the scan" };

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var uid = (await inbox.AppendAsync(message))!.Value;
        await inbox.CloseAsync();
        await inbox.OpenAsync(FolderAccess.ReadWrite);

        // FILE it into the repository: the document moves, Inbox keeps the shortcut — the designed behaviour
        // this test must NOT change.
        var repoFolder = await client.GetFolderAsync(repoName!);
        await inbox.CopyToAsync(uid, repoFolder);
        Guid docId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            var doc = await db.Documents.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(d => d.Name == "Contract scan");
            docId = doc.Id;
            Assert.Equal(repoId, doc.ParentId);
            Assert.True(await db.DocumentReferences.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(r => r.TargetDocumentId == docId), "filing must leave the shortcut in Inbox");
        }

        // MOVE the shortcut into an archive folder: the reference relocates; the document stays filed.
        await inbox.CloseAsync();
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var refUid = (await inbox.SearchAsync(MailKit.Search.SearchQuery.All)).Single();
        await inbox.MoveToAsync(refUid, sorted);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            Assert.Equal(repoId, (await db.Documents.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(d => d.Id == docId)).ParentId);
            var reference = await db.DocumentReferences.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(r => r.TargetDocumentId == docId);
            var holder = await db.Documents.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(d => d.Id == reference.ParentFolderId);
            Assert.Equal("Shortcuts", holder.Name);
        }

        // DELETE the shortcut where it now lives: the reference goes; the document still does not move.
        await sorted.OpenAsync(FolderAccess.ReadWrite);
        var inSorted = (await sorted.SearchAsync(MailKit.Search.SearchQuery.All)).Single();
        await sorted.AddFlagsAsync(inSorted, MessageFlags.Deleted, silent: true);
        await sorted.ExpungeAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            Assert.False(await db.DocumentReferences.IgnoreQueryFilters(["TenantFilter"]).AnyAsync(r => r.TargetDocumentId == docId));
            var doc = await db.Documents.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(d => d.Id == docId);
            Assert.Equal(repoId, doc.ParentId);
            Assert.Null(doc.DeletedAt);
        }

        await client.DisconnectAsync(true);
    }

    // The tenant default SEEDS a new user's IMAP view and owns nothing else (#793, ADR 0710). Three facts in
    // one arc, because each alone is passable by a wrong implementation: the default seeds a new user; an
    // EXISTING user's own choice survives a tenant-default change; and the seeded user can still override
    // themselves — it is a starting position, never a policy.
    [Fact]
    public async Task The_tenant_default_seeds_new_users_and_owns_nobody()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var adminEmail = $"imap-def-a-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "def-1234", "Default Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using (var scope = _factory.Services.CreateScope())
        {
            // The shared promote helper deliberately does not include user management; this test needs the
            // CREATE path specifically, since that is where the seed lives.
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            var promoted = await db.Users.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(u => u.NormalizedEmail == adminEmail.ToUpperInvariant());
            promoted.CanManageUsers = true;
            await db.SaveChangesAsync();
        }

        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "def-1234"));

        // The tenant arrives with the default ON (the store default) — visible in the settings resource.
        var settings = await TestJson.Get(admin, "/api/tenant-settings");
        Assert.True(settings.GetProperty("imapShowAllDocumentsDefault").GetBoolean());

        // A user created now is seeded ON…
        var seededOn = await TestJson.Post(admin, "/api/users",
            new { email = $"on-{Guid.NewGuid():N}@e2e.local", displayName = "Seeded On", password = "seed-1234" });
        Assert.True(seededOn.GetProperty("imapShowAllDocuments").GetBoolean());

        // …and after the tenant flips the default OFF, the next user is seeded OFF while the first keeps ON:
        // the default owns creation, never the person.
        (await admin.PutAsJsonAsync("/api/tenant-settings/mail", new { imapShowAllDocumentsDefault = false }))
            .EnsureSuccessStatusCode();
        var seededOff = await TestJson.Post(admin, "/api/users",
            new { email = $"off-{Guid.NewGuid():N}@e2e.local", displayName = "Seeded Off", password = "seed-1234" });
        Assert.False(seededOff.GetProperty("imapShowAllDocuments").GetBoolean());
        var firstAgain = await TestJson.Get(admin, $"/api/users/{seededOn.GetProperty("id").GetGuid()}");
        Assert.True(firstAgain.GetProperty("imapShowAllDocuments").GetBoolean());

        // …and the seeded user overrides their own seed — self-service survives (#793's whole premise).
        using var off = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(
            seededOff.GetProperty("email").GetString()!, "seed-1234"));
        (await off.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).EnsureSuccessStatusCode();
        var overridden = await TestJson.Get(admin, $"/api/users/{seededOff.GetProperty("id").GetGuid()}");
        Assert.True(overridden.GetProperty("imapShowAllDocuments").GetBoolean());
    }

}
