using SimplArchive.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using System.Text;
using SimplArchive.Api.Lmtp;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// ADR 0628: mail is DELIVERED, not fetched. An MTA terminates SMTP from the world and hands us LMTP on a
// private listener, and three rules are the whole of the delivery semantics — 250 only after the bytes are
// durable, 4xx for a temporary problem so the MTA holds the mail, 550 for an unknown recipient so the sender
// is told rather than the message vanishing.
//
// Driven over a real socket rather than by calling LmtpDelivery directly: the per-recipient reply after DATA
// is the part of RFC 2033 most easily got wrong, and it is only observable on the wire.
[Collection(E2ECollection.Name)]
public class LmtpDeliveryTests
{
    private readonly E2EApiFactory _factory;

    public LmtpDeliveryTests(E2EApiFactory factory) => _factory = factory;

    private int Port => ((LmtpServer)_factory.Services.GetService(typeof(LmtpServer))!).BoundPort!.Value;

    /// <summary>A tenant that claims a domain, and a user at it.</summary>
    private async Task<(Guid TenantId, string Address, string Domain)> RecipientAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        _ = clientId;
        _ = secret;

        var domain = $"lmtp-{Guid.NewGuid():N}".ToLowerInvariant()[..16] + ".test";
        var address = $"anna@{domain}";
        await _factory.SeedUserAsync(tenantId, address, "lmtp-1234", "Anna Lmtp");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            db.TenantMailDomains.Add(new TenantMailDomain
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Domain = domain,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return (tenantId, address, domain);
    }

    private sealed class Lmtp : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public Lmtp(int port)
        {
            _client = new TcpClient("127.0.0.1", port);
            var stream = _client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
        }

        public async Task<string> ReadAsync() => await _reader.ReadLineAsync() ?? string.Empty;

        public async Task SendAsync(string line) => await _writer.WriteLineAsync(line);

        /// <summary>Send a command and read one reply, skipping the multi-line continuations (`250-`).</summary>
        public async Task<string> ExchangeAsync(string line)
        {
            await SendAsync(line);
            var reply = await ReadAsync();
            while (reply.Length > 3 && reply[3] == '-')
            {
                reply = await ReadAsync();
            }

            return reply;
        }

        public void Dispose() => _client.Dispose();
    }

    private static string Message(string from, string to, string subject) =>
        $"From: {from}\r\nTo: {to}\r\nSubject: {subject}\r\n"
        + $"Message-ID: <{Guid.NewGuid():N}@test>\r\nDate: Mon, 17 Aug 2026 10:00:00 +0000\r\n\r\nBody text.\r\n";

    [Fact]
    public async Task A_message_for_a_known_recipient_is_accepted_and_filed()
    {
        var (_, address, _) = await RecipientAsync();

        using var lmtp = new Lmtp(Port);
        Assert.StartsWith("220", await lmtp.ReadAsync());
        Assert.StartsWith("250", await lmtp.ExchangeAsync("LHLO mta.test"));
        Assert.StartsWith("250", await lmtp.ExchangeAsync("MAIL FROM:<sender@example.test>"));
        Assert.StartsWith("250", await lmtp.ExchangeAsync($"RCPT TO:<{address}>"));
        Assert.StartsWith("354", await lmtp.ExchangeAsync("DATA"));

        var subject = $"Quarterly {Guid.NewGuid():N}"[..20];
        foreach (var line in Message("sender@example.test", address, subject).Split("\r\n"))
        {
            await lmtp.SendAsync(line);
        }

        Assert.StartsWith("250", await lmtp.ExchangeAsync("."));
        Assert.StartsWith("221", await lmtp.ExchangeAsync("QUIT"));

        // The 250 is a promise the bytes are durable, so the row must be there the moment it is sent.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var filed = db.Documents.IgnoreQueryFilters().SingleOrDefault(d => d.Name == subject);
        Assert.NotNull(filed);

        // Filed into INBOX under the lazily-created Mailbox node, not loose in the personal space.
        var inbox = db.Documents.IgnoreQueryFilters().Single(d => d.Id == filed!.ParentId);
        Assert.Equal("Inbox", inbox.Name); // PascalCase in the tree; IMAP projects it as INBOX (#596)
        var mailbox = db.Documents.IgnoreQueryFilters().Single(d => d.Id == inbox.ParentId);
        Assert.Equal("My Mailbox", mailbox.Name);
    }

    [Fact]
    public async Task An_unknown_recipient_is_refused_permanently_rather_than_silently_accepted()
    {
        var (_, _, domain) = await RecipientAsync();

        using var lmtp = new Lmtp(Port);
        await lmtp.ReadAsync();
        await lmtp.ExchangeAsync("LHLO mta.test");
        await lmtp.ExchangeAsync("MAIL FROM:<sender@example.test>");

        // The domain is ours; the local part is nobody's. 550 so the MTA bounces and the sender learns —
        // accepting it would put the message nowhere, silently, which is the failure ADR 0626 forbids.
        Assert.StartsWith("550", await lmtp.ExchangeAsync($"RCPT TO:<nobody@{domain}>"));

        // …and a domain no tenant claims is refused the same way.
        Assert.StartsWith("550", await lmtp.ExchangeAsync("RCPT TO:<anyone@not-ours.test>"));
    }

    [Fact]
    public async Task DATA_emits_ONE_reply_PER_RECIPIENT()
    {
        // The LMTP difference, and the bug that hides behind a single-recipient test: an implementation that
        // sends one reply for the message passes everything above and loses mail the moment two recipients
        // share a transaction, because the MTA reads the second reply as belonging to the next command.
        var (tenantId, first, domain) = await RecipientAsync();
        var second = $"tom@{domain}";
        await _factory.SeedUserAsync(tenantId, second, "lmtp-1234", "Tom Lmtp");

        using var lmtp = new Lmtp(Port);
        await lmtp.ReadAsync();
        await lmtp.ExchangeAsync("LHLO mta.test");
        await lmtp.ExchangeAsync("MAIL FROM:<sender@example.test>");
        Assert.StartsWith("250", await lmtp.ExchangeAsync($"RCPT TO:<{first}>"));
        Assert.StartsWith("250", await lmtp.ExchangeAsync($"RCPT TO:<{second}>"));
        Assert.StartsWith("354", await lmtp.ExchangeAsync("DATA"));

        var subject = $"Both {Guid.NewGuid():N}"[..16];
        foreach (var line in Message("sender@example.test", $"{first}, {second}", subject).Split("\r\n"))
        {
            await lmtp.SendAsync(line);
        }

        await lmtp.SendAsync(".");

        // TWO replies, one per accepted recipient.
        Assert.StartsWith("250", await lmtp.ReadAsync());
        Assert.StartsWith("250", await lmtp.ReadAsync());

        // …and it really was filed twice, once into each user's own inbox.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        Assert.Equal(2, db.Documents.IgnoreQueryFilters().Count(d => d.Name == subject));
    }

    [Fact]
    public async Task SMTPs_greeting_is_refused_rather_than_answered()
    {
        // A peer sending EHLO is speaking SMTP to an LMTP port. Answering it would let a misconfigured MTA
        // appear to work while the per-recipient semantics silently differ (RFC 2033 requires the refusal).
        using var lmtp = new Lmtp(Port);
        await lmtp.ReadAsync();
        Assert.StartsWith("500", await lmtp.ExchangeAsync("EHLO mta.test"));
    }



    [Fact]
    public async Task A_mailbox_created_under_the_old_name_is_renamed_on_the_next_delivery()
    {
        // The mailbox node is located by its MASK, so the 2026-08-19 rename of "My eMails" → "My Mailbox"
        // cannot orphan one. What it CAN do is let the names drift: a space that already had a mailbox would
        // keep the old name forever while newly created ones got the new one, and nothing would report it.
        //
        // A fresh-tenant run never sees this — it only ever creates the node under the new name — so the
        // pre-rename state is built deliberately here (#574's lesson).
        var (_, address, _) = await RecipientAsync();

        // Scoped to THIS user's mailbox by walking up from the message just delivered. Querying by name
        // globally passes in isolation and fails in the class run, because sibling tests have mailboxes too —
        // the test would then be reporting on somebody else's folder.
        var first = await DeliverOneAsync(address);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        var mailbox = MailboxOf(db, first);
        Assert.Equal("My Mailbox", mailbox.Name);

        mailbox.Name = "My eMails";
        await db.SaveChangesAsync();

        var second = await DeliverOneAsync(address);

        using var after = _factory.Services.CreateScope();
        var db2 = after.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        // Renamed in place: the SECOND message lands in the same node, now under the new name.
        var again = MailboxOf(db2, second);
        Assert.Equal(mailbox.Id, again.Id);
        Assert.Equal("My Mailbox", again.Name);
    }

    /// <summary>The mailbox node holding the message with this subject: message → INBOX → mailbox.</summary>
    private static Document MailboxOf(SimplArchiveDbContext db, string subject)
    {
        var filed = db.Documents.IgnoreQueryFilters().Single(d => d.Name == subject);
        var inbox = db.Documents.IgnoreQueryFilters().Single(d => d.Id == filed.ParentId);
        return db.Documents.IgnoreQueryFilters().Single(d => d.Id == inbox.ParentId);
    }

    /// <summary>One delivered message, for tests that care about the mailbox rather than the exchange.</summary>
    private async Task<string> DeliverOneAsync(string address)
    {
        using var lmtp = new Lmtp(Port);
        await lmtp.ReadAsync();
        await lmtp.ExchangeAsync("LHLO mta.test");
        await lmtp.ExchangeAsync("MAIL FROM:<sender@example.test>");
        await lmtp.ExchangeAsync($"RCPT TO:<{address}>");
        await lmtp.ExchangeAsync("DATA");

        var subject = $"Msg {Guid.NewGuid():N}"[..16];
        foreach (var line in Message("sender@example.test", address, subject).Split("\r\n"))
        {
            await lmtp.SendAsync(line);
        }

        Assert.StartsWith("250", await lmtp.ExchangeAsync("."));
        await lmtp.ExchangeAsync("QUIT");
        return subject;
    }

    [Fact]
    public async Task The_inbox_wears_the_ephemeral_mask_and_an_older_one_is_healed()
    {
        // The mask is what marks an INBOX EPHEMERAL (#596): its content lives under the `mail/` prefix and is
        // swept, and an `IMAP Folder` may never sit beneath it precisely because archive content must not hang
        // off an ephemeral parent. A maskless INBOX is therefore not just untyped — it is indistinguishable
        // from archive content to anything keying off the mask.
        var (_, address, _) = await RecipientAsync();

        var first = await DeliverOneAsync(address);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        var filed = db.Documents.IgnoreQueryFilters().Single(d => d.Name == first);
        var inbox = db.Documents.IgnoreQueryFilters().Single(d => d.Id == filed.ParentId);
        Assert.Equal("Inbox", inbox.Name); // PascalCase in the tree; IMAP projects it as INBOX (#596)
        Assert.NotNull(inbox.MaskVersionId);
        Assert.True(await IsImapSpecialAsync(db, inbox.MaskVersionId));

        // An INBOX created before the mask existed: a grow-only seed never revisits it, so the heal has to
        // happen on the next delivery. Via ExecuteUpdate, because the state is HISTORICAL, not a transition —
        // ADR 0685 now refuses moving a folder OFF a structural mask through SaveChanges, and a pre-mask
        // folder never made that transition: it simply predates the mask.
        await db.Documents.IgnoreQueryFilters().Where(d => d.Id == inbox.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.MaskVersionId, (Guid?)null));

        var second = await DeliverOneAsync(address);

        using var after = _factory.Services.CreateScope();
        var db2 = after.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var healed = db2.Documents.IgnoreQueryFilters().Single(d => d.Id == inbox.Id);
        Assert.True(await IsImapSpecialAsync(db2, healed.MaskVersionId));

        // …and the second message landed in that same healed INBOX rather than a new one beside it.
        var secondFiled = db2.Documents.IgnoreQueryFilters().Single(d => d.Name == second);
        Assert.Equal(inbox.Id, secondFiled.ParentId);
    }

    private static async Task<bool> IsImapSpecialAsync(SimplArchiveDbContext db, Guid? maskVersionId) =>
        maskVersionId is { } id
        && await db.MaskVersions.IgnoreQueryFilters()
            .AnyAsync(v => v.Id == id && v.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.ImapSpecial);
}
