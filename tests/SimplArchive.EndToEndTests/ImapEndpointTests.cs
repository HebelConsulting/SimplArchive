using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MailKit;
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
            using var put = new HttpRequestMessage(HttpMethod.Put, $"/webdav/{repoName}/{name}")
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
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/webdav/{repoName}/note.eml")
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
        var appendedSecond = await repo.AppendAsync(message);
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
}
