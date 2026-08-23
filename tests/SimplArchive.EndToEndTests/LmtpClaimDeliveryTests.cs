using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using System.Text;
using SimplArchive.Api.Lmtp;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// Delivery consults the address claims (#703 PR 3, ADR 0679): a recipient is accepted when a USER owns the
// address (the original rule, untouched) or when a live Mailbox's "eMail Addresses" list contains it. The
// claims here are seeded directly at the DbContext — deliberately: writing them through the API is ADR 0679's
// own test surface, and seeding two mailboxes with the same address is exactly the state a CONFIRMED
// duplicate leaves behind, which is the state fan-out delivery must handle.
[Collection(E2ECollection.Name)]
public class LmtpClaimDeliveryTests
{
    private readonly E2EApiFactory _factory;

    public LmtpClaimDeliveryTests(E2EApiFactory factory) => _factory = factory;

    private int Port => ((LmtpServer)_factory.Services.GetService(typeof(LmtpServer))!).BoundPort!.Value;

    /// <summary>A tenant claiming a domain, with one user whose personal mailbox is materialised.</summary>
    private async Task<(Guid TenantId, string Domain, Guid UserId, string UserAddress)> TenantWithUserAsync(string local = "anna")
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var domain = $"claim-{Guid.NewGuid():N}"[..16].ToLowerInvariant() + ".test";
        var address = $"{local}@{domain}";
        var userId = await _factory.SeedUserAsync(tenantId, address, "lmtp-1234", $"{local} claims");

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

        // A delivery to the user's own address is what materialises "My Mailbox" — the same trigger the
        // product uses (IMAP provisioning being the other).
        await DeliverAsync(address);
        return (tenantId, domain, userId, address);
    }

    /// <summary>Puts an address on a user's mailbox claims list, straight at the store.</summary>
    private async Task<Guid> ClaimAsync(Guid tenantId, Guid ownerId, string claimedAddress)
    {
        using var scope = _factory.Services.CreateScope();
        // SaveChanges' field validation reads tenant-filtered, and no request has set the tenant here.
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        var mailboxId = await db.Documents.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.PersonalRootOwnerId == ownerId && d.Name == "My Mailbox")
            .Select(d => d.Id)
            .SingleAsync();

        var fieldId = await db.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == tenantId && v.MaskId == WellKnownMaskIds.Mailbox && v.IsCurrent)
            .Join(db.FieldDefinitions.IgnoreQueryFilters(), v => v.Id, f => f.MaskVersionId, (_, f) => f)
            .Where(f => f.Name == WellKnownMaskSeeder.MailboxAddressesFieldName)
            .Select(f => f.Id)
            .SingleAsync();

        db.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = mailboxId,
            FieldDefinitionId = fieldId,
            Value = claimedAddress,
        });
        await db.SaveChangesAsync();
        return mailboxId;
    }

    // The socket harness, shared shape with LmtpDeliveryTests (copied like MultipartOf is between the import
    // tests — it is a few lines of wire plumbing, and a shared fixture would couple the two classes' runs).
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

    /// <summary>Delivers one message; returns its subject. Asserts the exchange succeeded.</summary>
    private async Task<string> DeliverAsync(string address)
    {
        using var lmtp = new Lmtp(Port);
        await lmtp.ReadAsync();
        await lmtp.ExchangeAsync("LHLO mta.test");
        await lmtp.ExchangeAsync("MAIL FROM:<sender@example.test>");
        Assert.StartsWith("250", await lmtp.ExchangeAsync($"RCPT TO:<{address}>"));
        await lmtp.ExchangeAsync("DATA");

        var subject = $"Claim {Guid.NewGuid():N}"[..18];
        await lmtp.SendAsync($"From: sender@example.test\r\nTo: {address}\r\nSubject: {subject}\r\n\r\nBody.");
        Assert.StartsWith("250", await lmtp.ExchangeAsync("."));
        await lmtp.ExchangeAsync("QUIT");
        return subject;
    }

    [Fact]
    public async Task A_claimed_address_delivers_into_the_claiming_users_inbox()
    {
        var (tenantId, domain, userId, userAddress) = await TenantWithUserAsync();
        await ClaimAsync(tenantId, userId, $"events@{domain}");

        // The claimed address — which no USER owns — is accepted and lands in the claiming mailbox's inbox.
        var subject = await DeliverAsync($"events@{domain}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var filed = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Name == subject);
        var inbox = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == filed.ParentId);
        Assert.Equal("Inbox", inbox.Name);
        Assert.Equal(userId, inbox.PersonalRootOwnerId);

        // …and the user branch is untouched: their own address still delivers exactly as before.
        var direct = await DeliverAsync(userAddress);
        Assert.NotNull(await db.Documents.IgnoreQueryFilters().SingleOrDefaultAsync(d => d.Name == direct));
    }

    [Fact]
    public async Task A_confirmed_duplicate_fans_out_one_copy_per_claiming_mailbox()
    {
        var (tenantId, domain, annaId, _) = await TenantWithUserAsync();
        var tomAddress = $"tom@{domain}";
        var tomId = await _factory.SeedUserAsync(tenantId, tomAddress, "lmtp-1234", "tom claims");
        await DeliverAsync(tomAddress); // materialise tom's mailbox

        // BOTH mailboxes claim the address — the state an explicitly confirmed duplicate leaves (ADR 0679).
        await ClaimAsync(tenantId, annaId, $"sales@{domain}");
        await ClaimAsync(tenantId, tomId, $"SALES@{domain}"); // case-insensitive on purpose

        // ONE recipient, ONE 250 — several targets behind one RCPT still answer as one (the per-recipient
        // reply discipline concerns multiple RCPTs, not multiple targets).
        var subject = await DeliverAsync($"sales@{domain}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var copies = await db.Documents.IgnoreQueryFilters().Where(d => d.Name == subject).ToListAsync();

        // One copy in each claimant's inbox — fan-out is the feature the admin confirmed, not duplication.
        Assert.Equal(2, copies.Count);
        var owners = new HashSet<Guid?>();
        foreach (var copy in copies)
        {
            var inbox = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == copy.ParentId);
            Assert.Equal("Inbox", inbox.Name);
            owners.Add(inbox.PersonalRootOwnerId);
        }

        Assert.Equal([annaId, tomId], owners.Select(o => o!.Value).OrderBy(o => annaId == o ? 0 : 1).ToList());
    }

    [Fact]
    public async Task A_recycled_mailboxs_claim_stops_receiving()
    {
        var (tenantId, domain, userId, _) = await TenantWithUserAsync();
        var mailboxId = await ClaimAsync(tenantId, userId, $"gone@{domain}");

        // Recycle the mailbox at the store — the state a deleted department mailbox (#703 PR 4) will be in.
        // Via ExecuteUpdate, deliberately: BOTH gates refuse recycling a personal standing folder (the REST
        // path and the #596 SaveChanges invariant), so the state is unreachable through the product today —
        // this simulates the PR 4 future rather than exercising a path that exists.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            await db.Documents.IgnoreQueryFilters().Where(d => d.Id == mailboxId)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.DeletedAt, DateTimeOffset.UtcNow));
        }

        // The claim is dead the moment the mailbox is recycled: 550, so the sender LEARNS — a 250 into a
        // recycle bin would be mail vanishing with a receipt (the ADR 0626 test).
        using var lmtp = new Lmtp(Port);
        await lmtp.ReadAsync();
        await lmtp.ExchangeAsync("LHLO mta.test");
        await lmtp.ExchangeAsync("MAIL FROM:<sender@example.test>");
        Assert.StartsWith("550", await lmtp.ExchangeAsync($"RCPT TO:<gone@{domain}>"));
    }

    [Fact]
    public async Task A_deactivated_owners_claim_is_excluded_from_delivery()
    {
        var (tenantId, domain, userId, _) = await TenantWithUserAsync();
        await ClaimAsync(tenantId, userId, $"orphan@{domain}");

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var owner = await db.Users.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(u => u.Id == userId);
            owner.IsActive = false;
            await db.SaveChangesAsync();
        }

        // A deactivated user's mailbox not accepting mail is the user branch's own rule, and a claim must not
        // become the way around it: 550, visibly, rather than filing into a space nobody reads.
        using var lmtp = new Lmtp(Port);
        await lmtp.ReadAsync();
        await lmtp.ExchangeAsync("LHLO mta.test");
        await lmtp.ExchangeAsync("MAIL FROM:<sender@example.test>");
        Assert.StartsWith("550", await lmtp.ExchangeAsync($"RCPT TO:<orphan@{domain}>"));
    }
}
