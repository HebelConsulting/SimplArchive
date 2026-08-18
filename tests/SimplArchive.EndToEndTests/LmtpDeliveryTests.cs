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
        Assert.Equal("INBOX", inbox.Name);
        var mailbox = db.Documents.IgnoreQueryFilters().Single(d => d.Id == inbox.ParentId);
        Assert.Equal("My eMails", mailbox.Name);
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
}
