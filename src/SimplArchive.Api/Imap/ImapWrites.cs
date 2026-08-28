using Microsoft.EntityFrameworkCore;
using MimeKit;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Api.Controllers;
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
        string? messageId;
        try
        {
            var parsed = MimeMessage.Load(new MemoryStream(bytes));
            subject = parsed.Subject ?? string.Empty;
            // The SAME normalizer that stores the Entry ID at finalize (#704). A second one here is exactly how
            // the stored form and the queried form drift and the correlation below silently never fires.
            messageId = Infrastructure.Storage.EmailMetadataExtractor.NormalizeMessageId(parsed.MessageId);
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

        // The Notes flow (#562 slice 5): a NoteFolder-typed mailbox correlates by the client's UUID header —
        // Apple Notes EDITS by appending a new message and deleting the old, so a UUID match becomes a new
        // VERSION of the existing note document instead of a sibling.
        var folderIsNotes = await db.MaskVersions.AnyAsync(v => v.Id == folder.MaskVersionId && v.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.Notebook);
        if (folderIsNotes)
        {
            await AppendNoteAsync(session, scope, tag, db, folder, mailbox, stem, bytes, userId, tenantId);
            return;
        }

        // An .eml ALREADY IN THIS FOLDER carrying the same Message-ID: the append is a re-filing of one message,
        // so it becomes a new VERSION of that document rather than a sibling (#780). Same shape as the Notes
        // correlation above, and for the same reason — a client that re-uploads (a resync, a second drag, a
        // rule that fires twice) otherwise multiplies documents silently.
        //
        // Deliberately scoped to THIS FOLDER. Tenant-wide correlation was considered and rejected: an APPEND
        // into folder B would attach a version to a document living in folder A, where the caller may hold
        // different rights and will not see the result — surprising, and impossible to phrase in a refusal.
        // Cross-folder sameness is the duplicates probe's job (#704), and filing one mail into two folders is
        // legitimately two documents (IMAP COPY is how a client asks for one).
        //
        // Identity is the eMail mask's "Entry ID" field, matched by NAME — the same key the seeder, the
        // finalizer and DuplicatesController use.
        var existingId = messageId is null
            ? null
            : await db.FieldValues
                .Where(v => v.Value == messageId)
                .Join(db.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { v.DocumentId, f.Name, f.MaskVersionId })
                .Where(x => x.Name == "Entry ID")
                .Join(db.MaskVersions, x => x.MaskVersionId, mv => mv.Id, (x, mv) => new { x.DocumentId, mv.MaskId })
                .Where(x => x.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.EMail)
                .Where(x => db.Documents.Any(d => d.Id == x.DocumentId && d.ParentId == folder.Id))
                .Select(x => (Guid?)x.DocumentId)
                .FirstOrDefaultAsync();

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        Document document;
        if (existingId is { } matchedId)
        {
            // The version lands under the document's OWN storage folder, so a version's artifacts stay nested
            // with its siblings rather than starting a second tree for the same document.
            document = await db.Documents.FirstAsync(d => d.Id == matchedId);
        }
        else
        {
            var siblings = await db.Documents.Where(d => d.ParentId == folder.Id).Select(d => d.Name).ToListAsync();
            var name = stem;
            for (var i = 2; siblings.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
            {
                name = $"{stem} ({i})";
            }

            // The WebDAV PUT's exact create shape (ADR 0530 keys; Pending version -> shared finalizer).
            document = new Document { Id = Guid.NewGuid(), TenantId = tenantId, ParentId = folder.Id, Name = name, CreatedByUserId = userId, CreatedAt = now, StorageFolderId = Guid.NewGuid() };
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
        }

        // The key follows the FOLDER's tier (#802). An APPEND into a staging folder stores on the ephemeral
        // mail key, exactly as LMTP delivery does — bytes live where their folder lives, so a later move to
        // Trash is ephemeral→ephemeral and a filing into the repository re-keys, both as designed. The old
        // unconditional archive key put appended mail on the wrong tier: expunging it to Trash was refused by
        // the mover's one-way rule ("archive content cannot enter mail storage"), surfacing as NO server error.
        var objectKey = await EphemeralMailFolder.IsEphemeralAsync(db, folder.Id)
            ? ObjectKeyBuilder.EphemeralMailKey(tenantId, userId, document.StorageFolderId, versionId, ".eml")
            : ObjectKeyBuilder.Build(tenantId, document.CreatedAt, document.StorageFolderId, versionId, ".eml");
        await storage.PutObjectAsync(objectKey, new MemoryStream(bytes), "message/rfc822");

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
                .GetPreviewUrlAsync(objectKey, TimeSpan.FromMinutes(1), document.Name + ".eml");
        }
        catch (Exception)
        {
            // The preview stays on-demand; the document is filed either way.
        }

        // Hand the UID back (APPENDUID, RFC 4315) — clients use it to adopt the appended message without a
        // refetch.
        //
        // A correlated re-append KEEPS the document's existing UID, which is where this deliberately parts from
        // the Notes path above. Notes bumps the UID because an edit there is append-then-DELETE, so the old
        // message must stop being listed. An .eml re-append has no paired delete: bumping would make the
        // message vanish and reappear in every connected client, churning caches to say nothing had changed.
        int uid;
        var uidRow = existingId is null
            ? null
            : await db.ImapMessageUids.FirstOrDefaultAsync(u => u.FolderId == folder.Id && u.DocumentId == document.Id);
        if (uidRow is not null)
        {
            uid = uidRow.Uid;
        }
        else
        {
            uid = mailbox.NextUid;
            mailbox.NextUid++;
            db.ImapMessageUids.Add(new Domain.Imap.ImapMessageUid { FolderId = folder.Id, DocumentId = document.Id, TenantId = tenantId, Uid = uid });
        }

        await db.SaveChangesAsync();

        // Every user-facing mutation is audited (#562 slice 4) — same action the workbench filing records. The
        // note says WHICH of the two happened: an administrator reading the log must be able to tell a filing
        // from a re-filing that added a version, since only the second leaves the document count unchanged.
        await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
            .RecordAsync(AuditActions.DocumentFiled, "Document", document.Id, document.Name,
                existingId is null ? "Filed over IMAP" : "Re-filed over IMAP (same Message-ID — new version)");
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

            // Absorption (#562 slice 5): a notes client edits by APPEND-new + delete-old. The re-append moved
            // the document's UID forward, so a session holding the OLD uid is deleting a superseded message,
            // not the note — absorb it (drop from the listing, keep the document and its new version).
            var currentUid = await db.ImapMessageUids
                .Where(u => u.FolderId == selected.FolderId && u.DocumentId == message.DocumentId)
                .Select(u => (int?)u.Uid).FirstOrDefaultAsync();
            if (currentUid is { } cu && cu != message.Uid)
            {
                expungedSequences.Add(index + 1); // it disappears from the client's view like any expunge
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

            // The document does not LIVE in this folder. Two different facts wear that shape, and they get
            // two different answers (#802). An entry backed by a REFERENCE deletes as the reference — the
            // shortcut goes, the document does not learn it happened (the WebDAV #769 rule). Anything else is
            // the stale second half of a client's move emulation — a COPY-that-organizes already moved the
            // document, and the expunge aimed at the superseded source entry — absorbed, touching nothing,
            // or it would yank the freshly organized mail out of its archive folder into Trash.
            if (document.ParentId != selected.FolderId)
            {
                var shortcut = await db.DocumentReferences.FirstOrDefaultAsync(
                    r => r.ParentFolderId == selected.FolderId && r.TargetDocumentId == message.DocumentId);
                if (shortcut is not null)
                {
                    db.DocumentReferences.Remove(shortcut);
                    await db.SaveChangesAsync();
                    await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
                        .RecordAsync(AuditActions.ReferenceRemoved, "Document", document.Id, document.Name, "Reference removed over IMAP (EXPUNGE)");
                }

                expungedSequences.Add(index + 1);
                continue;
            }

            // WHERE the delete happens decides what it MEANS (#658). In a mail client, deleting outside Trash
            // moves the message to Trash and deleting inside Trash is final — that is what every user expects,
            // and doing the final thing everywhere made a mis-click in Inbox unrecoverable from the client.
            //
            // Null means "final here": either this IS Trash, or it is an ordinary archive folder, where a
            // delete keeps its existing meaning (soft-delete into the recycle bin, as the workbench does).
            // Moving an ARCHIVED document into the mail Trash would be far worse than the bug — it would pull
            // it out of the repository and hand it to the sweep that empties that prefix.
            if (await EphemeralMailFolder.TrashForDeleteAsync(db, selected.FolderId) is { } trashFolderId)
            {
                // Two messages sharing a subject is ordinary in mail — a thread is a pile of "Re: the thing" —
                // and the sibling-name invariant refuses the second. Without a free name, deleting the second
                // message of a thread fails while the first succeeds.
                document.Name = await EphemeralMailFolder.FreeNameAsync(db, trashFolderId, document.Name);

                // Ephemeral → ephemeral: no re-key, but the mover RESTARTS the retention clock, which is what
                // makes "30 days in Trash" mean 30 days since it was put there rather than since it arrived.
                await scope.ServiceProvider.GetRequiredService<Documents.DocumentMover>()
                    .RelocateContentForMoveAsync(document.Id, trashFolderId, CancellationToken.None);

                document.ParentId = trashFolderId;
                await db.SaveChangesAsync();
                await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
                    .RecordAsync(AuditActions.DocumentMoved, "Document", document.Id, document.Name, "Moved to Trash over IMAP (EXPUNGE)");
                expungedSequences.Add(index + 1);
                continue;
            }

            document.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
                .RecordAsync(AuditActions.DocumentDeleted, "Document", document.Id, document.Name, "Deleted over IMAP (EXPUNGE)");
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

            // A REFERENCE-backed entry (#802, second live find): the listing projects a document referenced
            // into this folder as a message, and nothing marked it — so MOVE re-parented the TARGET document
            // out of the repository, and the within-tier COPY hauled it into a mail folder. Acting on an
            // appearance acts on the APPEARANCE: the reference row relocates (or duplicates, for a true copy),
            // and the document does not learn it happened — the same rule WebDAV's DELETE follows (#769).
            var appearance = await db.DocumentReferences.FirstOrDefaultAsync(
                r => r.ParentFolderId == selected.FolderId && r.TargetDocumentId == message.DocumentId);
            if (appearance is not null
                && !await db.Documents.AnyAsync(d => d.Id == message.DocumentId && d.ParentId == selected.FolderId))
            {
                var duplicate = await db.DocumentReferences.AnyAsync(
                    r => r.ParentFolderId == targetFolderId && r.TargetDocumentId == message.DocumentId);
                var alreadyHome = await db.Documents.AnyAsync(
                    d => d.Id == message.DocumentId && d.ParentId == targetFolderId);
                if (move || await EphemeralMailFolder.IsEphemeralAsync(db, targetFolderId))
                {
                    // Moving the shortcut — explicitly (MOVE), or the mail client's drag between mail folders,
                    // which arrives as COPY and means move (the emulation this whole branch family serves).
                    if (duplicate || alreadyHome)
                    {
                        db.DocumentReferences.Remove(appearance); // the target already shows it — collapse
                    }
                    else
                    {
                        appearance.ParentFolderId = targetFolderId;
                    }

                    await db.SaveChangesAsync();
                    await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
                        .RecordAsync(AuditActions.ReferenceMoved, "Document", message.DocumentId, message.Name, "Reference moved over IMAP");
                    if (move)
                    {
                        movedSequences.Add(sequence);
                    }
                    else
                    {
                        remaining.Add(message);
                    }
                }
                else
                {
                    // A true COPY of the shortcut into an archive folder: a second appearance.
                    if (!duplicate && !alreadyHome)
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
                        await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
                            .RecordAsync(AuditActions.ReferenceAdded, "Document", message.DocumentId, message.Name, "Referenced over IMAP (COPY)");
                    }

                    remaining.Add(message);
                }

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
                try
                {
                    // Filing a message out of INBOX moves its BYTES onto archive storage (#633) — the case this
                    // whole seam exists for, since the inbox is where messages arrive. Before the save, so a
                    // refused move leaves the message addressing its original content.
                    await scope.ServiceProvider.GetRequiredService<Documents.DocumentMover>()
                        .RelocateContentForMoveAsync(document.Id, targetFolderId, CancellationToken.None);
                }
                catch (Errors.Exceptions.Documents.CannotFileIntoEphemeralMailException)
                {
                    remaining.Add(message); // an archived document cannot be moved back into the inbox
                    continue;
                }

                document.ParentId = targetFolderId;
                try
                {
                    await db.SaveChangesAsync();
                    await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
                        .RecordAsync(AuditActions.DocumentMoved, "Document", document.Id, document.Name, "Moved over IMAP");
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
                // COPY files a REFERENCE (#562): the document stays home, the target folder gains a shortcut —
                // which is right for an item ALREADY IN THE REPOSITORY, and wrong for one that is not.
                //
                // Ephemeral means "not yet in the repository" (#640). A staged message has no home in the
                // archive to stay in, so leaving it where it is and pointing a shortcut at it makes an ARCHIVE
                // folder's content depend on storage whose whole purpose is to be emptied — the shortcut breaks
                // the day the sweep runs. Same shape ADR 0634 forbids for a folder under an ephemeral parent,
                // by reference rather than by parent, so no invariant would have caught it.
                //
                // So COPY OUT OF A STAGING FOLDER files for real: the document moves into the target (bytes
                // re-keyed onto archive storage by DocumentMover) and the STASH keeps the shortcut. Copying
                // between archive folders is untouched.
                var mover = scope.ServiceProvider.GetRequiredService<Documents.DocumentMover>();
                if (await EphemeralMailFolder.IsEphemeralAsync(db, selected.FolderId))
                {
                    var document = await db.Documents.FirstAsync(d => d.Id == message.DocumentId);
                    await mover.RelocateContentForMoveAsync(document.Id, targetFolderId, CancellationToken.None);
                    document.ParentId = targetFolderId;

                    // WITHIN the staging tier — Inbox to a user's Archive folder — the shortcut is withheld
                    // (#802, live find): organizing means the mail LEAVES Inbox, and a reference left behind is
                    // the opposite of the promise. The shortcut belongs to the other case only: filing into
                    // the REPOSITORY, where the mailbox keeping a pointer at the filed document is the feature.
                    var withinMailTier = await EphemeralMailFolder.IsEphemeralAsync(db, targetFolderId);
                    if (!withinMailTier)
                    {
                        // The stash keeps a shortcut, so the message still shows in the mail client where the
                        // user left it, now pointing at the filed document rather than being it.
                        db.DocumentReferences.Add(new DocumentReference
                        {
                            Id = Guid.NewGuid(),
                            TenantId = tenantId,
                            ParentFolderId = selected.FolderId,
                            TargetDocumentId = message.DocumentId,
                            CreatedByUserId = userId,
                            CreatedAt = DateTimeOffset.UtcNow,
                        });
                    }

                    try
                    {
                        await db.SaveChangesAsync();
                        await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
                            .RecordAsync(AuditActions.DocumentFiled, "Document", document.Id, document.Name,
                                withinMailTier ? "Moved between mail folders over IMAP (COPY)" : "Filed out of mail storage over IMAP (COPY)");
                    }
                    catch (InvalidOperationException)
                    {
                        db.Entry(document).State = EntityState.Unchanged;
                        document.ParentId = selected.FolderId;
                    }

                    remaining.Add(message);
                    continue;
                }

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
                    await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
                        .RecordAsync(AuditActions.ReferenceAdded, "Document", message.DocumentId, message.Name, "Referenced over IMAP (COPY)");
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

    // ---- Notes (#562 slice 5, ADR "IMAP endpoint: Notes") --------------------------------------------

    /// <summary>Creates a user mail folder under the archive subtree (#802).</summary>
    private static async Task CreateImapFolderAsync(ImapSession session, IServiceScope scope, string tag, string[] segments)
    {
        // The parent is everything but the last segment, and it must already exist — clients create nested
        // paths one level at a time, so inventing intermediates would let a typo build a tree.
        var parentName = string.Join('/', segments[..^1]);
        var leaf = segments[^1];

        var parent = await ImapMailboxes.ResolveAsync(session, scope, ImapProtocol.EncodeModifiedUtf7(parentName));
        if (parent is null)
        {
            await session.WriteLineAsync($"{tag} NO [TRYCREATE] no such parent mailbox");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        var tenantId = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().TenantId!.Value;

        if (!(await calculator.GetEffectiveRightsAsync(userId, parent.Value.Mailbox.FolderId)).CanCreateSubItems)
        {
            await session.WriteLineAsync($"{tag} NO you cannot create a folder here");
            return;
        }

        var maskVersionId = await FolderMask.CurrentVersionIdAsync(
            db, tenantId, SimplArchive.Domain.Masks.WellKnownMaskIds.ImapFolder, CancellationToken.None);

        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = parent.Value.Mailbox.FolderId,
            Name = leaf,
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            StorageFolderId = Guid.NewGuid(),
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (InvalidOperationException)
        {
            // The sibling-name invariant: the mailbox already has one. RFC 3501 calls this a NO, and naming
            // the reason beats the blanket sentence — a client shows this text to the user.
            await session.WriteLineAsync($"{tag} NO a mailbox with that name already exists");
            return;
        }

        await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
            .RecordAsync(AuditActions.DocumentCreated, "Document", db.Documents.Local.First(d => d.Name == leaf).Id, leaf,
                "Mail folder created over IMAP");
        await session.WriteLineAsync($"{tag} OK CREATE completed");
    }

    /// <summary>True when the mailbox is a user-created mail folder — the only kind DELETE/RENAME touch.</summary>
    /// <remarks>
    /// Asked of the MASK, not the path: a folder reached as Archive/Work is deletable because it IS an
    /// IMAP Folder, and a provisioned mailbox is not because it is not — the same one-answer rule as the
    /// ephemeral tier's, so renaming the archive or a future second creatable subtree cannot silently widen
    /// or narrow what these verbs act on (#802).
    /// </remarks>
    private static async Task<bool> IsUserMailFolderAsync(SimplArchiveDbContext db, Guid folderId)
    {
        var maskId = await db.Documents
            .Where(d => d.Id == folderId)
            .Select(d => db.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault())
            .FirstOrDefaultAsync();
        return maskId == SimplArchive.Domain.Masks.WellKnownMaskIds.ImapFolder;
    }

    /// <summary>DELETE of a user mail folder: the subtree soft-deletes into the recycle bin (#802).</summary>
    /// <remarks>
    /// Soft, deliberately — a folder of mail a user tidies away from their phone must be recoverable, and the
    /// recycle bin is where every other delete in this product goes. Refused for everything that is not a
    /// user folder, with the same sentence as before: the rest of the tree is managed in the workbench.
    /// </remarks>
    internal static async Task DeleteMailboxAsync(ImapSession session, IServiceScope scope, string tag, string arguments)
    {
        var tokens = ImapProtocol.Tokenize(arguments);
        var resolved = tokens.Count >= 1 ? await ImapMailboxes.ResolveAsync(session, scope, tokens[0]) : null;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        if (resolved is null || !await IsUserMailFolderAsync(db, resolved.Value.Mailbox.FolderId))
        {
            await session.WriteLineAsync($"{tag} NO the folder structure is managed in SimplArchive, not over IMAP");
            return;
        }

        var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        if (!(await calculator.GetEffectiveRightsAsync(userId, resolved.Value.Mailbox.FolderId)).CanDelete)
        {
            await session.WriteLineAsync($"{tag} NO you cannot delete this mailbox");
            return;
        }

        // The CASCADE the workbench delete performs, and the same two gates: a folder of mail is a subtree,
        // and deleting the folder alone would leave its messages alive under a deleted parent — invisible
        // everywhere, restorable nowhere.
        var document = await db.Documents.FirstAsync(d => d.Id == resolved.Value.Mailbox.FolderId);
        var toDelete = await db.CollectSubtreeAsync(document.Id, document, CancellationToken.None);
        if (toDelete.Any(d => d.CheckedOutByUserId is { } holder && holder != userId))
        {
            await session.WriteLineAsync($"{tag} NO a document in this mailbox is checked out by someone else");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var doc in toDelete)
        {
            doc.DeletedAt = now;
        }

        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
            .RecordAsync(AuditActions.DocumentDeleted, "Document", document.Id, document.Name, "Mail folder deleted over IMAP");
        await session.WriteLineAsync($"{tag} OK DELETE completed");
    }

    /// <summary>RENAME of a user mail folder: the document renames, the subtree rides along (#802).</summary>
    /// <remarks>
    /// The destination must stay inside the archive subtree and renames only the LEAF — a rename that would
    /// re-parent (RFC 3501 allows "RENAME a/b c/d") is refused rather than half-honoured, because a move has
    /// its own semantics (re-keying, audit) that a rename must not silently perform.
    /// </remarks>
    internal static async Task RenameMailboxAsync(ImapSession session, IServiceScope scope, string tag, string arguments)
    {
        var tokens = ImapProtocol.Tokenize(arguments);
        var resolved = tokens.Count >= 2 ? await ImapMailboxes.ResolveAsync(session, scope, tokens[0]) : null;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        if (resolved is null || !await IsUserMailFolderAsync(db, resolved.Value.Mailbox.FolderId))
        {
            await session.WriteLineAsync($"{tag} NO the folder structure is managed in SimplArchive, not over IMAP");
            return;
        }

        var oldName = ImapProtocol.DecodeModifiedUtf7(tokens[0]).TrimEnd('/');
        var newName = ImapProtocol.DecodeModifiedUtf7(tokens[1]).TrimEnd('/');
        var oldSegments = oldName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var newSegments = newName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (newSegments.Length != oldSegments.Length
            || !oldSegments[..^1].SequenceEqual(newSegments[..^1], StringComparer.Ordinal))
        {
            await session.WriteLineAsync($"{tag} NO RENAME may change the name, not the place — move messages instead");
            return;
        }

        var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        if (!(await calculator.GetEffectiveRightsAsync(userId, resolved.Value.Mailbox.FolderId)).CanEditIndexData)
        {
            await session.WriteLineAsync($"{tag} NO you cannot rename this mailbox");
            return;
        }

        var document = await db.Documents.FirstAsync(d => d.Id == resolved.Value.Mailbox.FolderId);
        var previousName = document.Name;
        document.Name = newSegments[^1];
        try
        {
            await db.SaveChangesAsync();
        }
        catch (InvalidOperationException)
        {
            await session.WriteLineAsync($"{tag} NO a mailbox with that name already exists");
            return;
        }

        await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
            .RecordAsync(AuditActions.DocumentRenamed, "Document", document.Id, document.Name,
                $"Mail folder renamed over IMAP (was: {previousName})");
        await session.WriteLineAsync($"{tag} OK RENAME completed");
    }

    private static async Task AppendNoteAsync(
        ImapSession session, IServiceScope scope, string tag, SimplArchiveDbContext db,
        Document folder, Domain.Imap.ImapMailbox mailbox, string stem, byte[] bytes, Guid userId, Guid tenantId)
    {
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var mime = MimeMessage.Load(new MemoryStream(bytes));
        // The correlation key: Apple's X-Universally-Unique-Identifier, else the Message-ID, else fresh.
        var uuid = mime.Headers["X-Universally-Unique-Identifier"] ?? mime.MessageId ?? Guid.NewGuid().ToString();
        var modified = (mime.Date == default ? DateTimeOffset.UtcNow : mime.Date).UtcDateTime.ToString("yyyy-MM-dd");

        var noteMaskVersionId = await db.MaskVersions
            .Where(v => v.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.Note && v.IsCurrent)
            .Select(v => v.Id).SingleAsync();
        var fieldIds = await db.FieldDefinitions
            .Where(f => f.MaskVersionId == noteMaskVersionId)
            .ToDictionaryAsync(f => f.Name, f => f.Id);

        // An existing note with this UUID in this folder → the append IS an edit: a new version, same identity.
        //
        // PAST the soft-delete filter, deliberately (#790). A notes client edits in either order, and only
        // APPEND-then-DELETE was survivable: a client that DELETES the old message first really soft-deletes
        // the note, and a correlation that cannot see the deleted row then forks a NEW document for the
        // replacement — the note "disappears" and returns as a stranger. The tenant filter stays enforced;
        // only the soft-delete veil lifts, and only for the question "did this UUID ever live here".
        var existingId = await db.FieldValues
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(fv => fv.FieldDefinitionId == fieldIds["Note UUID"] && fv.Value == uuid
                && db.Documents.Any(d => d.Id == fv.DocumentId && d.ParentId == folder.Id))
            .Select(fv => (Guid?)fv.DocumentId)
            .FirstOrDefaultAsync();

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        Document document;
        if (existingId is { } id)
        {
            document = await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).FirstAsync(d => d.Id == id);

            // The delete half of the edit already landed: bring the note back before attaching its new
            // version. The append IS the proof the client still has the note — restoring is not second-
            // guessing a deletion, it is completing the edit the two verbs together mean. The full restorer
            // rather than a bare DeletedAt reset, so a gone parent and the search index are handled the same
            // way a workbench restore handles them.
            if (document.DeletedAt is not null)
            {
                await scope.ServiceProvider.GetRequiredService<Documents.DocumentRestorer>()
                    .RestoreAsync(document, userId, null, CancellationToken.None);
            }
        }
        else
        {
            var siblings = await db.Documents.Where(d => d.ParentId == folder.Id).Select(d => d.Name).ToListAsync();
            var name = stem;
            for (var i = 2; siblings.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
            {
                name = $"{stem} ({i})";
            }

            // The Note mask + its REQUIRED UUID field land in the same save (ADR 0176 validates required
            // fields on mask assignment) — and the containment invariant sees Note-in-NoteFolder, so it holds.
            document = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ParentId = folder.Id,
                Name = name,
                MaskVersionId = noteMaskVersionId,
                CreatedByUserId = userId,
                CreatedAt = now,
                StorageFolderId = Guid.NewGuid(),
            };
            db.Documents.Add(document);
            db.FieldValues.Add(new SimplArchive.Domain.Masks.FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = document.Id, FieldDefinitionId = fieldIds["Note UUID"], Value = uuid });
            db.FieldValues.Add(new SimplArchive.Domain.Masks.FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = document.Id, FieldDefinitionId = fieldIds["Modified"], Value = modified });
        }

        var objectKey = ObjectKeyBuilder.Build(tenantId, document.CreatedAt, document.StorageFolderId, versionId, ".eml");
        await storage.PutObjectAsync(objectKey, new MemoryStream(bytes), "message/rfc822");
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = tenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        });

        if (existingId is not null)
        {
            var modifiedValue = await db.FieldValues.FirstOrDefaultAsync(fv => fv.DocumentId == document.Id && fv.FieldDefinitionId == fieldIds["Modified"]);
            if (modifiedValue is null)
            {
                db.FieldValues.Add(new SimplArchive.Domain.Masks.FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = document.Id, FieldDefinitionId = fieldIds["Modified"], Value = modified });
            }
            else
            {
                modifiedValue.Value = modified;
            }
        }

        await db.SaveChangesAsync();
        var version = await db.DocumentVersions.FirstAsync(v => v.Id == versionId);
        await scope.ServiceProvider.GetRequiredService<DocumentFinalizer>().FinalizeAsync(version, CancellationToken.None);

        // A stable identity gets a NEW UID per re-append (the row updates in place — the PK is per document,
        // UIDs only ever grow). The old message's UID vanishes from later listings; an EXPUNGE aimed at it is
        // ABSORBED (see ExpungeAsync) — the paired delete of an edit must not soft-delete the updated note.
        var uid = mailbox.NextUid;
        mailbox.NextUid++;
        var uidRow = await db.ImapMessageUids.FirstOrDefaultAsync(u => u.FolderId == folder.Id && u.DocumentId == document.Id);
        if (uidRow is null)
        {
            db.ImapMessageUids.Add(new Domain.Imap.ImapMessageUid { FolderId = folder.Id, DocumentId = document.Id, TenantId = tenantId, Uid = uid });
        }
        else
        {
            uidRow.Uid = uid;
        }

        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IAuditRecorder>()
            .RecordAsync(AuditActions.DocumentFiled, "Document", document.Id, document.Name,
                existingId is null ? "Note filed over IMAP" : "Note updated over IMAP");
        await session.WriteLineAsync($"{tag} OK [APPENDUID {mailbox.UidValidity} {uid}] APPEND completed");
    }

    // ---- CREATE (notebook sections only) -------------------------------------------------------------

    // The mailbox tree IS the archive tree and stays read-only over IMAP (#562) — with ONE opening, decided in
    // #564: a section inside the notebook. Apple Notes sorts notes into subfolders, so a notebook that cannot
    // grow one is a notebook the client cannot actually use, and refusing here is refusing the feature rather
    // than protecting anything. The confinement is the point: the target must resolve under the Notes mailbox,
    // and what gets created is a NotebookSection, never a plain folder.
    internal static async Task CreateAsync(ImapSession session, IServiceScope scope, string tag, string arguments)
    {
        var tokens = ImapProtocol.Tokenize(arguments);
        if (tokens.Count < 1)
        {
            await session.WriteLineAsync($"{tag} BAD CREATE expects a mailbox name");
            return;
        }

        var name = ImapProtocol.DecodeModifiedUtf7(tokens[0]).TrimEnd('/');
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isNotes = segments.Length >= 1 && string.Equals(segments[0], "Notes", StringComparison.Ordinal);
        var isArchive = segments.Length >= 2 && string.Equals(segments[0], "Archive", StringComparison.Ordinal);
        if (!isNotes && !isArchive)
        {
            await session.WriteLineAsync($"{tag} NO the folder structure is managed in SimplArchive, not over IMAP");
            return;
        }

        // `CREATE "Archive/<name>"` — a user folder in the mailbox's own organizational space (#802). The
        // archive root itself is provisioned, so only children are creatable, and the LIST attributes say
        // exactly that: the read-only tree wears \Noinferiors, the archive subtree does not.
        if (isArchive)
        {
            await CreateImapFolderAsync(session, scope, tag, segments);
            return;
        }

        // `CREATE "Notes"` — the FIRST thing a notes client does on an account it has not used before, and the
        // reason notes were unavailable at all: the notebook is not provisioned, so without this the client
        // asks for the one folder it needs and is refused (#596). It lands under the mailbox, which the user's
        // IMAP credential has already materialised, and the cardinality rule keeps it at one.
        if (segments.Length == 1)
        {
            await CreateNotebookAsync(session, scope, tag);
            return;
        }

        // The parent is everything but the last segment, and it must already exist — IMAP clients create a
        // nested path one level at a time, so inventing the intermediates here would let a typo build a tree.
        var parentName = string.Join('/', segments[..^1]);
        var leaf = segments[^1];

        var parent = await ImapMailboxes.ResolveAsync(session, scope, ImapProtocol.EncodeModifiedUtf7(parentName));
        if (parent is null)
        {
            await session.WriteLineAsync($"{tag} NO [TRYCREATE] no such parent mailbox");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        var tenantId = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().TenantId!.Value;

        if (!(await calculator.GetEffectiveRightsAsync(userId, parent.Value.Mailbox.FolderId)).CanCreateSubItems)
        {
            await session.WriteLineAsync($"{tag} NO you cannot create a section here");
            return;
        }

        var maskVersionId = await FolderMask.CurrentVersionIdAsync(
            db, tenantId, SimplArchive.Domain.Masks.WellKnownMaskIds.NotebookSection, CancellationToken.None);

        var sectionId = Guid.NewGuid();
        db.Documents.Add(new Document
        {
            Id = sectionId,
            TenantId = tenantId,
            ParentId = parent.Value.Mailbox.FolderId,
            Name = leaf,
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (InvalidOperationException)
        {
            // Sibling-name clash, or containment refusing the placement — either way the client's remedy is a
            // different name or a different parent, and IMAP has one status for both.
            await session.WriteLineAsync($"{tag} NO could not create '{leaf}' there");
            return;
        }

        // RFC 8474 §5 (#780) — the id is the section's own, handed back without a round trip to fetch it.
        await session.WriteLineAsync($"{tag} OK [MAILBOXID ({ImapObjectId.ForMailbox(sectionId)})] CREATE completed");
    }

    // The notebook itself, as opposed to a section inside it. Separate from the path above because there is no
    // parent to resolve and nothing to name: where it goes and what it is called are both fixed, so the whole
    // of the work is "make sure it exists", which is what makes re-issuing CREATE harmless.
    private static async Task CreateNotebookAsync(ImapSession session, IServiceScope scope, string tag)
    {
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        var tenantId = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().TenantId!.Value;
        var mailbox = scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.PersonalMailboxProvisioner>();

        Guid notebookId;
        try
        {
            notebookId = await mailbox.EnsureNotebookAsync(tenantId, userId, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Containment or cardinality refused it — a second notebook, or a personal space in a shape the
            // invariants do not allow. The client's remedy is the same either way, and IMAP has one status.
            await session.WriteLineAsync($"{tag} NO could not create 'Notes'");
            return;
        }

        // RFC 8474 §5 returns the new mailbox's id on CREATE, saving the client a SELECT purely to learn it.
        // Re-issuing CREATE is harmless AND now informative: an existing notebook answers with the SAME id,
        // which is how a client confirms the notebook it already knows is the one it just asked for (#780).
        await session.WriteLineAsync($"{tag} OK [MAILBOXID ({ImapObjectId.ForMailbox(notebookId)})] CREATE completed");
    }

}
