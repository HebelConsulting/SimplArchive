using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Imap;

/// <summary>
/// The mailbox lifecycle verbs: CREATE, DELETE and RENAME (ADR "IMAP endpoint").
/// </summary>
/// <remarks>
/// <para>
/// Split out of <c>ImapMailboxes</c> at 917 lines. CLAUDE.md asks that a class be split by responsibility as
/// it APPROACHES the limit rather than after crossing it, and this was the cohesive part: what remains there
/// answers questions about mailboxes -- the catalog, resolution, SELECT/STATUS, the UID assignment -- while
/// these six MUTATE the tree behind them.
/// </para>
/// <para>
/// Its own type rather than a partial, unlike the two clients' view models: those share instance state, so a
/// partial was the honest answer there. This is a static class with none, and CLAUDE.md's rule says extract
/// the cohesive part into its own TYPE. Three call sites moved, all in <c>ImapSession</c>'s command dispatch.
/// </para>
/// </remarks>
internal static class ImapMailboxLifecycle
{
    //
    // Moved here from ImapWrites (the 1000-line debt list, #909): these are commands about a MAILBOX, and this
    // file is where mailboxes are answered — LIST, STATUS and SELECT already read the catalog these mutate.
    // The old home split by read-vs-write, which this file never did anyway (MarkSeenAsync is a write and has
    // always lived here), and what that split cost was distance: "what does CREATE accept?" was answered two
    // files away from "what does LIST advertise?", while ImapMailboxEntry.AcceptsChildren — right at the top of
    // this file — is the rule BOTH have to agree on. #792 is what happens when they drift.

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
