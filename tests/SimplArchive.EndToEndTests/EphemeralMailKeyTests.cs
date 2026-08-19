using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using System.Text;
using SimplArchive.Api.Lmtp;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// An inbox is not an archive: deleting there is just deleting, with no retention schedule and no disposition
// review (ADR 0628). That promise is only real if the BYTES live outside the archive's rules too — so a
// delivered message goes to tenants/{t}/users/{u}/mail/, and filing it out has to move the object, not just
// the row (#633).
//
// The failure this guards is the nastiest shape a bug can have: leaving the key alone produces archive
// documents whose content still sits in ephemeral storage — correct until the sweep runs, and then silently
// unreadable. So these tests assert the bytes are READABLE at the new key, never merely that the key changed.
[Collection(E2ECollection.Name)]
public class EphemeralMailKeyTests
{
    private readonly E2EApiFactory _factory;

    public EphemeralMailKeyTests(E2EApiFactory factory) => _factory = factory;

    private int Port => ((LmtpServer)_factory.Services.GetService(typeof(LmtpServer))!).BoundPort!.Value;

    private async Task<(Guid TenantId, Guid UserId, string Address)> RecipientAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var domain = $"ephem-{Guid.NewGuid():N}".ToLowerInvariant()[..17] + ".test";
        var address = $"anna@{domain}";
        var userId = await _factory.SeedUserAsync(tenantId, address, "ephem-1234", "Anna Ephemeral");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        db.TenantMailDomains.Add(new TenantMailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Domain = domain,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return (tenantId, userId, address);
    }

    /// <summary>Delivers one message over the real LMTP socket and returns the filed document's id + subject.</summary>
    private async Task<(Guid DocumentId, string Subject)> DeliverAsync(string address)
    {
        using var client = new TcpClient("127.0.0.1", Port);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        // Skips the multi-line continuations (`250-`), like LmtpDeliveryTests' own helper — LHLO answers with
        // several lines, and reading one leaves every later read a reply behind. That desync is why the first
        // version of this test filed nothing and blamed the delivery.
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

        var subject = $"Ephemeral {Guid.NewGuid():N}"[..24];
        foreach (var line in new[]
                 {
                     "From: sender@example.test",
                     $"To: {address}",
                     $"Subject: {subject}",
                     "",
                     "The body that has to survive being filed.",
                 })
        {
            await writer.WriteLineAsync(line);
        }

        Assert.StartsWith("250", await Exchange("."));
        await Exchange("QUIT");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var filed = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Name == subject);
        return (filed.Id, subject);
    }

    private static async Task<string> KeyOfAsync(E2EApiFactory factory, Guid documentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        return await db.DocumentVersions.IgnoreQueryFilters()
            .Where(v => v.DocumentId == documentId)
            .Select(v => v.ObjectKey)
            .SingleAsync();
    }

    [Fact]
    public async Task A_delivered_message_lands_on_the_ephemeral_prefix()
    {
        var (tenantId, userId, address) = await RecipientAsync();
        var (documentId, _) = await DeliverAsync(address);

        var key = await KeyOfAsync(_factory, documentId);

        Assert.True(ObjectKeyBuilder.IsEphemeralMailKey(key),
            $"A delivered message was stored at '{key}', which is an archive key — so the inbox's promise (no "
            + "retention, no disposition review, deleting is just deleting) is a statement about the folder's "
            + "mask and not about where the bytes live (ADR 0628).");
        Assert.Contains($"tenants/{tenantId}/users/{userId}/mail/", key);
    }

    [Fact]
    public async Task Filing_a_message_out_of_the_inbox_moves_its_bytes_and_they_stay_readable()
    {
        var (tenantId, _, address) = await RecipientAsync();
        var (documentId, _) = await DeliverAsync(address);

        var ephemeralKey = await KeyOfAsync(_factory, documentId);
        Assert.True(ObjectKeyBuilder.IsEphemeralMailKey(ephemeralKey));

        // File it into My Documents — the ordinary destination for anything a user keeps.
        Guid myDocumentsId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var inboxId = (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == documentId)).ParentId!.Value;
            var mailboxId = (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == inboxId)).ParentId!.Value;
            var personalId = (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == mailboxId)).ParentId!.Value;
            myDocumentsId = await db.Documents.IgnoreQueryFilters()
                .Where(d => d.ParentId == personalId && d.Name == PersonalFolders.MyDocuments)
                .Select(d => d.Id).SingleAsync();

            var mover = scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.DocumentMover>();
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;

            Assert.True(await mover.RelocateContentForMoveAsync(documentId, myDocumentsId, CancellationToken.None));

            var document = await db.Documents.SingleAsync(d => d.Id == documentId);
            document.ParentId = myDocumentsId;
            await db.SaveChangesAsync();
        }

        var archiveKey = await KeyOfAsync(_factory, documentId);
        Assert.False(ObjectKeyBuilder.IsEphemeralMailKey(archiveKey),
            $"A message filed into the archive still points at '{archiveKey}'. It reads correctly today and "
            + "becomes unreadable the moment the ephemeral prefix is swept — the exact failure #633 exists to "
            + "prevent, and one that no test asserting only the ParentId would see.");
        Assert.StartsWith($"tenants/{tenantId}/", archiveKey);

        // The half that matters: the BYTES are at the new key. Asserting the key changed would pass just as
        // well if the copy had silently failed.
        using (var scope = _factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
            using var content = await storage.GetObjectAsync(archiveKey);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer);
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            Assert.Contains("The body that has to survive being filed.", text);
        }
    }

    [Fact]
    public async Task An_archived_document_cannot_be_moved_back_into_the_inbox()
    {
        var (tenantId, _, address) = await RecipientAsync();
        var (documentId, _) = await DeliverAsync(address);

        Guid inboxId, myDocumentsId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            inboxId = (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == documentId)).ParentId!.Value;
            var mailboxId = (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == inboxId)).ParentId!.Value;
            var personalId = (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == mailboxId)).ParentId!.Value;
            myDocumentsId = await db.Documents.IgnoreQueryFilters()
                .Where(d => d.ParentId == personalId && d.Name == PersonalFolders.MyDocuments)
                .Select(d => d.Id).SingleAsync();

            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var mover = scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.DocumentMover>();
            await mover.RelocateContentForMoveAsync(documentId, myDocumentsId, CancellationToken.None);
            var document = await db.Documents.SingleAsync(d => d.Id == documentId);
            document.ParentId = myDocumentsId;
            await db.SaveChangesAsync();
        }

        // Filing out is ONE-WAY. Re-keying backwards would put archived content where the sweep deletes it, and
        // would hand back retention/WORM/disposition the document has already acquired.
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var mover = scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.DocumentMover>();

            await Assert.ThrowsAsync<SimplArchive.Api.Errors.Exceptions.Documents.CannotFileIntoEphemeralMailException>(
                () => mover.RelocateContentForMoveAsync(documentId, inboxId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_message_moved_between_mail_folders_keeps_its_ephemeral_key()
    {
        // The inbox is the only IMAP Special folder today, so "between ephemeral folders" is exercised as the
        // no-op move back onto itself: what is asserted is that staying inside mail storage does NOT re-key,
        // since a re-key there would quietly archive bytes the user has not filed.
        var (tenantId, _, address) = await RecipientAsync();
        var (documentId, _) = await DeliverAsync(address);

        var before = await KeyOfAsync(_factory, documentId);

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var inboxId = (await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == documentId)).ParentId!.Value;

            var mover = scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.DocumentMover>();
            Assert.False(await mover.RelocateContentForMoveAsync(documentId, inboxId, CancellationToken.None));
        }

        Assert.Equal(before, await KeyOfAsync(_factory, documentId));
    }

    [Fact]
    public void The_inbox_folder_is_what_marks_storage_ephemeral()
    {
        // The predicate is asked of a KEY, by callers that hold only a version — so it has to recognise one
        // without rebuilding the prefix from ids. Both halves stated, because a predicate that answered "true"
        // for everything would pass every assertion above.
        var key = ObjectKeyBuilder.EphemeralMailKey(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".eml");
        Assert.True(ObjectKeyBuilder.IsEphemeralMailKey(key));
        Assert.EndsWith(".eml", key);

        var archive = ObjectKeyBuilder.Build(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), ".pdf");
        Assert.False(ObjectKeyBuilder.IsEphemeralMailKey(archive));

        // And the mask that says a folder is ephemeral is the one the mover asks about.
        Assert.Contains(WellKnownMaskIds.NoSubfolderMasks, m => m.FolderMaskId == WellKnownMaskIds.ImapSpecial);
    }
}
