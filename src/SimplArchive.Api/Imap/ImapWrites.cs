using Microsoft.EntityFrameworkCore;
using MimeKit;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Imap;

// The write commands (#562 slice 3, ADR "IMAP endpoint: the writes"): APPEND files an .eml through the same
// finalizer every upload path shares (classification, attachments, indexing) and eagerly renders its preview
// so embedded images are persisted; EXPUNGE turns the session's \Deleted marks into SOFT deletes; MOVE sets a
// new parent; COPY files a reference. Every write is gated by the same effective rights the workbench checks.
internal static class ImapWrites
{
    // ---- APPEND --------------------------------------------------------------------------------------

    internal static async Task AppendAsync(ImapSession session, IServiceScope scope, string tag, string arguments)
    {
        // APPEND <mailbox> [(flags)] [date-time] <message literal>. The literal reader has already turned the
        // message into the LAST quoted token; the optional middle tokens are accepted and ignored.
        var tokens = ImapProtocol.Tokenize(arguments);
        if (tokens.Count < 2)
        {
            await session.WriteLineAsync($"{tag} BAD APPEND expects a mailbox and a message");
            return;
        }

        var resolved = await ImapMailboxes.ResolveAsync(session, scope, tokens[0]);
        if (resolved is null)
        {
            await session.WriteLineAsync($"{tag} NO [TRYCREATE] no such mailbox");
            return;
        }

        var (mailbox, _) = resolved.Value;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        var tenantId = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().TenantId!.Value;

        var folder = await db.Documents.FirstAsync(d => d.Id == mailbox.FolderId);
        if (folder.PersonalOfUserId != userId && !(await calculator.GetEffectiveRightsAsync(userId, folder.Id)).CanCreateSubItems)
        {
            await session.WriteLineAsync($"{tag} NO you may not file into this mailbox");
            return;
        }

        var bytes = System.Text.Encoding.Latin1.GetBytes(tokens[^1]);
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        if (!await scope.ServiceProvider.GetRequiredService<IStorageQuotaService>().CanStoreAsync(tenantId, bytes.Length))
        {
            await session.WriteLineAsync($"{tag} NO the tenant's storage quota is exhausted");
            return;
        }

        // Name = the Subject (the workbench's display name for an email), sanitized of the characters the
        // path-addressed surfaces refuse, sibling-conflict auto-suffixed (#562).
        string subject;
        try
        {
            subject = MimeMessage.Load(new MemoryStream(bytes)).Subject ?? "";
        }
        catch (Exception)
        {
            await session.WriteLineAsync($"{tag} NO the message could not be parsed");
            return;
        }

        var stem = subject.Trim().Replace('/', '-');
        if (stem.Length == 0)
        {
            stem = "Message";
        }

        var siblings = await db.Documents.Where(d => d.ParentId == folder.Id).Select(d => d.Name).ToListAsync();
        var name = stem;
        for (var i = 2; siblings.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
        {
            name = $"{stem} ({i})";
        }

        // The WebDAV PUT's exact create shape (ADR 0530 keys; Pending version -> shared finalizer).
        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var storageFolderId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(tenantId, now, storageFolderId, versionId, ".eml");
        await storage.PutObjectAsync(objectKey, new MemoryStream(bytes), "message/rfc822");

        var document = new Document { Id = Guid.NewGuid(), TenantId = tenantId, ParentId = folder.Id, Name = name, CreatedByUserId = userId, CreatedAt = now, StorageFolderId = storageFolderId };
        db.Documents.Add(document);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (InvalidOperationException)
        {
            await session.WriteLineAsync($"{tag} NO a sibling with that name appeared concurrently");
            return;
        }

        var version = new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = tenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        db.DocumentVersions.Add(version);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<DocumentFinalizer>().FinalizeAsync(version, CancellationToken.None);

        // Eagerly render the preview so the email's embedded images are persisted in the rendition NOW (#562),
        // not on the first workbench click. Best-effort: a converter outage must not fail the filing.
        try
        {
            await scope.ServiceProvider.GetRequiredService<IDocumentPreviewService>()
                .GetPreviewUrlAsync(objectKey, TimeSpan.FromMinutes(1), name + ".eml");
        }
        catch (Exception)
        {
            // The preview stays on-demand; the document is filed either way.
        }

        // Hand the new UID back (APPENDUID, RFC 4315) — clients use it to adopt the appended message without
        // a refetch.
        var uid = mailbox.NextUid;
        mailbox.NextUid++;
        db.ImapMessageUids.Add(new Domain.Imap.ImapMessageUid { FolderId = folder.Id, DocumentId = document.Id, TenantId = tenantId, Uid = uid });
        await db.SaveChangesAsync();
        await session.WriteLineAsync($"{tag} OK [APPENDUID {mailbox.UidValidity} {uid}] APPEND completed");
    }

    // ---- EXPUNGE -------------------------------------------------------------------------------------

    internal static async Task ExpungeAsync(ImapSession session, IServiceScope scope, string tag, ImapSelectedMailbox selected)
    {
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;

        var expungedSequences = new List<int>();
        var remaining = new List<ImapMessageEntry>();
        for (var index = 0; index < selected.Messages.Count; index++)
        {
            var message = selected.Messages[index];
            if (!selected.DeletedDocumentIds.Contains(message.DocumentId))
            {
                remaining.Add(message);
                continue;
            }

            // The same gates the workbench delete applies: CanDelete, and never under an active legal hold.
            var held = await db.LegalHoldItems.AnyAsync(i =>
                i.DocumentId == message.DocumentId && db.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null));
            if (held || !(await calculator.GetEffectiveRightsAsync(userId, message.DocumentId)).CanDelete)
            {
                remaining.Add(message); // stays; RFC lets an EXPUNGE remove only what it may
                continue;
            }

            var document = await db.Documents.FirstAsync(d => d.Id == message.DocumentId);
            document.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            expungedSequences.Add(index + 1);
        }

        // Untagged EXPUNGE responses, highest sequence first, so each number is valid at the moment it is sent.
        foreach (var sequence in expungedSequences.OrderByDescending(s => s))
        {
            await session.WriteLineAsync($"* {sequence} EXPUNGE");
        }

        session.Selected = selected with { Messages = remaining };
        session.Selected.DeletedDocumentIds.Clear();
        await session.OkAsync(tag, "EXPUNGE");
    }

    // ---- MOVE / COPY ---------------------------------------------------------------------------------

    internal static async Task MoveOrCopyAsync(
        ImapSession session, IServiceScope scope, string tag, ImapSelectedMailbox selected, string arguments, bool uidMode, bool move)
    {
        var tokens = ImapProtocol.Tokenize(arguments);
        if (tokens.Count < 2)
        {
            await session.WriteLineAsync($"{tag} BAD expected a set and a target mailbox");
            return;
        }

        var target = await ImapMailboxes.ResolveAsync(session, scope, tokens[1]);
        if (target is null)
        {
            await session.WriteLineAsync($"{tag} NO [TRYCREATE] no such mailbox");
            return;
        }

        var targetFolderId = target.Value.Mailbox.FolderId;
        if (targetFolderId == selected.FolderId)
        {
            await session.WriteLineAsync($"{tag} NO source and target are the same mailbox");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        var tenantId = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().TenantId!.Value;

        var targetDoc = await db.Documents.FirstAsync(d => d.Id == targetFolderId);
        var mayFile = targetDoc.PersonalOfUserId == userId
            || (await calculator.GetEffectiveRightsAsync(userId, targetFolderId)).CanCreateSubItems;
        if (!mayFile)
        {
            await session.WriteLineAsync($"{tag} NO you may not file into the target mailbox");
            return;
        }

        var lastUid = selected.Messages.Count == 0 ? 0 : selected.Messages[^1].Uid;
        var movedSequences = new List<int>();
        var remaining = new List<ImapMessageEntry>();
        for (var index = 0; index < selected.Messages.Count; index++)
        {
            var message = selected.Messages[index];
            var sequence = index + 1;
            if (!ImapFetch.InSet(tokens[0], uidMode ? message.Uid : sequence, uidMode ? lastUid : selected.Messages.Count))
            {
                remaining.Add(message);
                continue;
            }

            if (move)
            {
                // MOVE = the workbench reparent (#562): a new ParentId, gated on CanMove; SaveChanges enforces
                // the sibling-name and cycle invariants — a refused move keeps the message where it is.
                if (!(await calculator.GetEffectiveRightsAsync(userId, message.DocumentId)).CanMove)
                {
                    remaining.Add(message);
                    continue;
                }

                var document = await db.Documents.FirstAsync(d => d.Id == message.DocumentId);
                document.ParentId = targetFolderId;
                try
                {
                    await db.SaveChangesAsync();
                    movedSequences.Add(sequence);
                }
                catch (InvalidOperationException)
                {
                    db.Entry(document).State = EntityState.Unchanged;
                    document.ParentId = selected.FolderId;
                    remaining.Add(message); // name clash in the target — the message stays
                }
            }
            else
            {
                // COPY files a REFERENCE (#562): the document stays home, the target folder gains a shortcut.
                var exists = await db.DocumentReferences.AnyAsync(r => r.ParentFolderId == targetFolderId && r.TargetDocumentId == message.DocumentId);
                if (!exists)
                {
                    db.DocumentReferences.Add(new DocumentReference
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        ParentFolderId = targetFolderId,
                        TargetDocumentId = message.DocumentId,
                        CreatedByUserId = userId,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                    await db.SaveChangesAsync();
                }

                remaining.Add(message);
            }
        }

        if (move)
        {
            foreach (var sequence in movedSequences.OrderByDescending(s => s))
            {
                await session.WriteLineAsync($"* {sequence} EXPUNGE");
            }

            session.Selected = selected with { Messages = remaining };
        }

        await session.OkAsync(tag, move ? "MOVE" : "COPY");
    }
}
