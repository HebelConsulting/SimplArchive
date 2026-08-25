using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Api.Imap;
using SimplArchive.Api.Lmtp;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// The five standing mailboxes (#596), and the question nobody had asked: **does a delivered message appear in
// the client's INBOX?**
//
// It did not. `INBOX` was the personal repository ROOT — decided in ADR 0594, before mail was delivered
// anywhere, when "the name every mail client knows" was the whole of the reasoning. Once LMTP began filing
// into `Personal/My Mailbox/Inbox`, that made the client's INBOX a folder which structurally CANNOT hold a
// message (the first level admits only the provisioned folders, #634), while the mail sat two levels down
// under a different name.
//
// Nothing errored. `ImapEndpointTests` asserted INBOX existed and listed folders; `LmtpDeliveryTests` asserted
// the document landed in the right place. Neither joined them, so a working account and a broken one produced
// identical logs — the same shape as the SEARCH bug, and the reason this file exists.
[Collection(E2ECollection.Name)]
public class ImapStandingMailboxTests
{
    // The personal space is named after its owner (ADR 0671), so its WebDAV/IMAP path segment is
    // whatever this test seeded as the display name — not the constant "Personal" it used to be.
    private const string Personal = "Anna Standing";

    private readonly E2EApiFactory _factory;

    public ImapStandingMailboxTests(E2EApiFactory factory) => _factory = factory;

    private int LmtpPort => ((LmtpServer)_factory.Services.GetService(typeof(LmtpServer))!).BoundPort!.Value;
    private int ImapPort => ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

    private async Task<string> DeliverAsync(string address, string subject)
    {
        using var client = new TcpClient("127.0.0.1", LmtpPort);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        async Task<string> Exchange(string line)
        {
            await writer.WriteLineAsync(line);
            var reply = await reader.ReadLineAsync() ?? string.Empty;
            while (reply.Length > 3 && reply[3] == '-')
            {
                reply = await reader.ReadLineAsync() ?? string.Empty;
            }

            return reply;
        }

        await reader.ReadLineAsync();
        await Exchange("LHLO mta.test");
        await Exchange("MAIL FROM:<sender@example.test>");
        await Exchange($"RCPT TO:<{address}>");
        await Exchange("DATA");
        foreach (var line in new[]
                 {
                     "From: sender@example.test", $"To: {address}", $"Subject: {subject}", "",
                     "Delivered, and it had better be visible.",
                 })
        {
            await writer.WriteLineAsync(line);
        }

        Assert.StartsWith("250", await Exchange("."));
        await Exchange("QUIT");
        return subject;
    }

    [Fact]
    public async Task A_delivered_message_appears_in_the_clients_INBOX_and_the_standing_mailboxes_advertise_their_use()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var domain = $"stand-{Guid.NewGuid():N}".ToLowerInvariant()[..17] + ".test";
        var address = $"anna@{domain}";
        const string password = "stand-1234";
        await _factory.SeedUserAsync(tenantId, address, password, "Anna Standing");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            db.TenantMailDomains.Add(new TenantMailDomain
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Domain = domain,
                CreatedAt = DateTimeOffset.UtcNow,
                // Delivery accepts only a VERIFIED domain (#667). These fixtures stand in for a
                // domain the operator declared, which arrives verified — the DNS challenge is the
                // route for one an administrator adds in the UI, and is exercised by
                // TenantMailDomainsTests rather than by every delivery test.
                VerifiedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(address, password));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        var subject = $"Standing {Guid.NewGuid():N}"[..22];
        await DeliverAsync(address, subject);

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", ImapPort, SecureSocketOptions.None);
        await client.AuthenticateAsync(address, imapPassword);

        var all = (await client.GetFoldersAsync(client.PersonalNamespaces[0])).ToList();
        var names = all.Select(f => f.FullName).ToList();

        // 1. The five exist at the ROOT, where a mail client looks — not buried under the archive tree.
        Assert.Contains("INBOX", names);
        foreach (var standing in new[] { "Drafts", "Sent", "Junk", "Trash" })
        {
            Assert.Contains(standing, names);
        }

        // 2. …and they say what they are FOR. Clients match on the RFC 6154 attribute, not the name: one told
        //    nothing helpfully creates its own "Sent Messages" beside ours, and then the user has two.
        Assert.True(all.Single(f => f.FullName == "Drafts").Attributes.HasFlag(FolderAttributes.Drafts));
        Assert.True(all.Single(f => f.FullName == "Sent").Attributes.HasFlag(FolderAttributes.Sent));
        Assert.True(all.Single(f => f.FullName == "Junk").Attributes.HasFlag(FolderAttributes.Junk));
        Assert.True(all.Single(f => f.FullName == "Trash").Attributes.HasFlag(FolderAttributes.Trash));

        // 3. THE ONE THAT MATTERS. Before #596 this failed: INBOX was the personal root, which cannot hold a
        //    message at all, so an account receiving mail showed an empty inbox and nothing said why.
        var inbox = await client.GetFolderAsync("INBOX");
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(1, inbox.Count);
        Assert.Equal(subject, (await inbox.GetMessageAsync(0)).Subject);

        // 4. The archive is still browsable — it simply stopped pretending to be an inbox.
        Assert.Contains(Personal, names);
        Assert.DoesNotContain("INBOX/My Mailbox/Inbox", names);

        await client.DisconnectAsync(true);
    }

    [Fact]
    public async Task A_user_can_make_their_own_mail_folder_beside_the_standing_five()
    {
        // Decided 2026-08-19 (#596): a Mailbox also admits an ordinary Folder, so filing mail into folders of
        // one's own works from IMAP and from the workbench alike — one create, one rel (ADR 0637).
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var email = $"ownfolder-{Guid.NewGuid():N}@e2e.local";
        const string password = "ownfld-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Own Folder");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", ImapPort, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        // Generating the credential is the mailbox's second trigger, so the standing five exist by now.
        var names = (await client.GetFoldersAsync(client.PersonalNamespaces[0])).Select(f => f.FullName).ToList();
        Assert.Contains("INBOX", names);
        Assert.Contains("Trash", names);

        await client.DisconnectAsync(true);
    }

    // Where a delete happens decides what it MEANS (#658). Every mail client teaches the same two rules —
    // delete outside Trash moves it to Trash, delete inside Trash is final — and EXPUNGE did the FINAL thing
    // everywhere, so a mis-click in the inbox was unrecoverable from the client. The message was in the
    // archive's recycle bin, which a mail client cannot see and most users do not know exists.
    [Fact]
    public async Task Deleting_outside_Trash_moves_to_Trash_and_deleting_in_Trash_is_final()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var domain = $"trash-{Guid.NewGuid():N}".ToLowerInvariant()[..17] + ".test";
        var address = $"tess@{domain}";
        const string password = "trash-1234";
        await _factory.SeedUserAsync(tenantId, address, password, "Tess Trash");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            db.TenantMailDomains.Add(new TenantMailDomain
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Domain = domain,
                CreatedAt = DateTimeOffset.UtcNow,
                // Delivery accepts only a VERIFIED domain (#667). These fixtures stand in for a
                // domain the operator declared, which arrives verified — the DNS challenge is the
                // route for one an administrator adds in the UI, and is exercised by
                // TenantMailDomainsTests rather than by every delivery test.
                VerifiedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(address, password));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        // Two messages sharing a subject, deliberately: a thread is a pile of "Re: the thing", and the
        // sibling-name invariant refuses a second document of the same name in one folder. Without a free name
        // the SECOND delete fails while the first succeeds — a mailbox that works once.
        var subject = $"Same subject {Guid.NewGuid():N}"[..24];
        await DeliverAsync(address, subject);
        await DeliverAsync(address, subject);

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", ImapPort, SecureSocketOptions.None);
        await client.AuthenticateAsync(address, imapPassword);

        var inbox = await client.GetFolderAsync("INBOX");
        var trash = await client.GetFolderAsync("Trash");
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        Assert.Equal(2, inbox.Count);

        // Delete BOTH in the inbox — they move to Trash rather than disappearing.
        await inbox.AddFlagsAsync(new[] { 0, 1 }, MessageFlags.Deleted, silent: true);
        await inbox.ExpungeAsync();
        Assert.Equal(0, inbox.Count);

        await trash.OpenAsync(FolderAccess.ReadWrite);
        Assert.Equal(2, trash.Count);

        // Still in the archive, not in the recycle bin: a delete in the inbox is a move, and the user can
        // fetch it back from the client itself.
        var bin = (await TestJson.Get(api, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(bin, i => i.GetProperty("name").GetString()!.StartsWith(subject, StringComparison.Ordinal));

        // …and now the SAME action in Trash is the final one.
        await trash.AddFlagsAsync(new[] { 0 }, MessageFlags.Deleted, silent: true);
        await trash.ExpungeAsync();
        Assert.Equal(1, trash.Count);

        var binAfter = (await TestJson.Get(api, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(binAfter, i => i.GetProperty("name").GetString()!.StartsWith(subject, StringComparison.Ordinal));

        await client.DisconnectAsync(true);
    }
}
