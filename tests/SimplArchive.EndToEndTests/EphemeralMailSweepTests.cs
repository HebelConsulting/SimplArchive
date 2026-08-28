using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Mail;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// The sweep that makes the ephemeral tier ephemeral (#640).
//
// The trap here is a sweep that is green because it swept NOTHING, so every case below states what survived as
// well as what went — and the survivals are the point. Only `Junk` and `Trash` are ever swept; `Inbox`,
// `Drafts` and `Sent` keep their mail forever, a legal hold outranks the window, and a message already filed
// into the archive must still be readable afterwards, which is the failure the re-key exists to prevent
// (ADR 0638) asserted from the other side.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class EphemeralMailSweepTests
{
    private readonly E2EApiFactory _factory;

    public EphemeralMailSweepTests(E2EApiFactory factory) => _factory = factory;

    /// <summary>A staged message in the named folder, aged past the window.</summary>
    private async Task<(Guid DocumentId, string ObjectKey)> StagedAsync(Guid tenantId, Guid userId, Guid mailboxId, string folderName, TimeSpan age)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;

        var folderId = await db.Documents.IgnoreQueryFilters()
            .Where(d => d.ParentId == mailboxId && d.Name == folderName)
            .Select(d => d.Id).SingleAsync();

        var storageFolderId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var key = ObjectKeyBuilder.EphemeralMailKey(tenantId, userId, storageFolderId, versionId, ".eml");
        await scope.ServiceProvider.GetRequiredService<IObjectStorageClient>()
            .PutObjectAsync(key, new MemoryStream("Subject: staged\r\n\r\nbody"u8.ToArray()), "message/rfc822");

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = folderId,
            Name = $"msg-{Guid.NewGuid():N}"[..14],
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            StorageFolderId = storageFolderId,
            StagedAt = DateTimeOffset.UtcNow - age,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        var version = new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = tenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = key,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        db.DocumentVersions.Add(version);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.DocumentFinalizer>().FinalizeAsync(version, CancellationToken.None);

        return (document.Id, key);
    }

    private async Task<(Guid TenantId, Guid UserId, Guid MailboxId)> MailboxAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"sweep-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "sweep-1234", "Sweep User");

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var mailbox = await scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.PersonalMailboxProvisioner>()
            .EnsureMailboxAsync(tenantId, userId, CancellationToken.None);

        return (tenantId, userId, mailbox.Id);
    }

    private Task SweepAsync() =>
        _factory.Services.GetRequiredService<EphemeralMailSweepWorker>().SweepAsync(CancellationToken.None);

    private async Task<bool> ExistsAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        return await db.Documents.IgnoreQueryFilters().AnyAsync(d => d.Id == documentId);
    }

    private async Task<bool> ObjectExistsAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IObjectStorageClient>().ExistsAsync(key);
    }

    [Fact]
    public async Task Only_Junk_and_Trash_are_swept_and_the_rest_keep_their_mail_forever()
    {
        var (tenantId, userId, mailboxId) = await MailboxAsync();
        var old = TimeSpan.FromDays(400);

        var trash = await StagedAsync(tenantId, userId, mailboxId, "Trash", old);
        var junk = await StagedAsync(tenantId, userId, mailboxId, "Junk", old);
        var inbox = await StagedAsync(tenantId, userId, mailboxId, "Inbox", old);
        var drafts = await StagedAsync(tenantId, userId, mailboxId, "Drafts", old);
        var sent = await StagedAsync(tenantId, userId, mailboxId, "Sent", old);

        await SweepAsync();

        // Gone — rows AND bytes. A sweep that dropped the row and left the object would grow the prefix it
        // exists to empty.
        Assert.False(await ExistsAsync(trash.DocumentId));
        Assert.False(await ExistsAsync(junk.DocumentId));
        Assert.False(await ObjectExistsAsync(trash.ObjectKey));
        Assert.False(await ObjectExistsAsync(junk.ObjectKey));

        // …and the three that are never swept, however old. This is the half that would pass trivially if the
        // sweep were broken, which is exactly why it is asserted alongside.
        Assert.True(await ExistsAsync(inbox.DocumentId), "Inbox was swept — it must keep its mail forever (#640).");
        Assert.True(await ExistsAsync(drafts.DocumentId), "Drafts was swept — it must keep its mail forever (#640).");
        Assert.True(await ExistsAsync(sent.DocumentId), "Sent was swept — it must keep its mail forever (#640).");
        Assert.True(await ObjectExistsAsync(inbox.ObjectKey));
    }

    [Fact]
    public async Task A_message_inside_its_window_is_left_alone()
    {
        var (tenantId, userId, mailboxId) = await MailboxAsync();
        var fresh = await StagedAsync(tenantId, userId, mailboxId, "Trash", TimeSpan.FromDays(2));

        await SweepAsync();

        Assert.True(await ExistsAsync(fresh.DocumentId), "A message two days in Trash was swept; the window is 30 days.");
        Assert.True(await ObjectExistsAsync(fresh.ObjectKey));
    }

    [Fact]
    public async Task A_legal_hold_outranks_the_retention_window()
    {
        var (tenantId, userId, mailboxId) = await MailboxAsync();
        var held = await StagedAsync(tenantId, userId, mailboxId, "Trash", TimeSpan.FromDays(400));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var hold = new SimplArchive.Domain.LegalHolds.LegalHold
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Sweep hold",
                PlacedByUserId = userId,
                PlacedAt = DateTimeOffset.UtcNow,
            };
            db.LegalHolds.Add(hold);
            db.LegalHoldItems.Add(new SimplArchive.Domain.LegalHolds.LegalHoldItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                LegalHoldId = hold.Id,
                DocumentId = held.DocumentId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await SweepAsync();

        // A hold is not a retention policy: being outside the archive's retention rules does not put a message
        // outside a hold, and a sweep that ignored one would be an invisible compliance failure.
        Assert.True(await ExistsAsync(held.DocumentId), "A message under a legal hold was swept.");
        Assert.True(await ObjectExistsAsync(held.ObjectKey));
    }

    [Fact]
    public async Task An_object_left_behind_by_filing_is_reclaimed_while_the_filed_document_stays_readable()
    {
        // The load-bearing half. DocumentMover deliberately leaves the ephemeral copy behind when a message is
        // filed out (ADR 0638), so with only the folder sweep every filed message would strand one forever.
        var (tenantId, userId, mailboxId) = await MailboxAsync();
        var staged = await StagedAsync(tenantId, userId, mailboxId, "Inbox", TimeSpan.FromDays(1));

        Guid myDocumentsId;
        string archiveKey;
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var personalId = (await db.Documents.SingleAsync(d => d.Id == mailboxId)).ParentId!.Value;
            myDocumentsId = await db.Documents.Where(d => d.ParentId == personalId && d.Name == PersonalFolders.MyDocuments)
                .Select(d => d.Id).SingleAsync();

            await scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.DocumentMover>()
                .RelocateContentForMoveAsync(staged.DocumentId, myDocumentsId, CancellationToken.None);
            var document = await db.Documents.SingleAsync(d => d.Id == staged.DocumentId);
            document.ParentId = myDocumentsId;
            await db.SaveChangesAsync();

            archiveKey = await db.DocumentVersions.Where(v => v.DocumentId == staged.DocumentId)
                .Select(v => v.ObjectKey).SingleAsync();
        }

        Assert.NotEqual(staged.ObjectKey, archiveKey);
        Assert.True(await ObjectExistsAsync(staged.ObjectKey), "The ephemeral copy should still be there before the sweep.");

        await SweepAsync();

        // The stranded copy is gone…
        Assert.False(await ObjectExistsAsync(staged.ObjectKey), "The ephemeral copy filing left behind was not reclaimed — the prefix grows forever.");

        // …and the filed document is untouched and still readable. This is the assertion that would fail if the
        // orphan collector keyed off the prefix without checking what claims each object.
        Assert.True(await ExistsAsync(staged.DocumentId));
        Assert.True(await ObjectExistsAsync(archiveKey), "The sweep deleted the bytes of a document that had been FILED — the exact failure #633 exists to prevent.");

        // Filing cleared the clock, so the folder half can never see it again either.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            Assert.Null((await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == staged.DocumentId)).StagedAt);
        }
    }
}
