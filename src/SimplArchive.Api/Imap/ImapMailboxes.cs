using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Imap;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Imap;

/// <summary>One mailbox in the catalog: its wire name and the folder Document behind it.</summary>
internal sealed record ImapMailboxEntry(string Name, Guid FolderId, bool HasChildren);

/// <summary>The SELECTed mailbox's session snapshot: sequence numbers are positions in <see cref="Messages"/>.
/// DeletedDocumentIds is the session-transient \Deleted staging (#562 slice 3) — nothing happens before
/// EXPUNGE; ReadOnly marks an EXAMINE, whose mailbox refuses STORE and EXPUNGE per RFC 3501.</summary>
internal sealed record ImapSelectedMailbox(Guid FolderId, string Name, IReadOnlyList<ImapMessageEntry> Messages, bool ReadOnly = false)
{
    public HashSet<Guid> DeletedDocumentIds { get; } = [];
}

/// <summary>One message of a selected mailbox: the document, its stable UID, and the current version's essentials.</summary>
internal sealed record ImapMessageEntry(Guid DocumentId, int Uid, string Name, string Extension, string ObjectKey, long? SizeBytes, DateTimeOffset InternalDate);

// The mailbox side of the IMAP endpoint (ADR "IMAP endpoint (read-only, first slice)"): the catalog (LIST),
// STATUS, SELECT/EXAMINE and the lazy UID assignment. The tree is the WebDAV mount's minus what a mail client
// has no use for (#562): INBOX = the personal repository root, shared repositories as top-level mailboxes, no
// Intray and no Check-out.
internal static class ImapMailboxes
{
    internal static async Task ListAsync(ImapSession session, IServiceScope scope, string tag, string command, string arguments)
    {
        var tokens = ImapProtocol.Tokenize(arguments);
        var reference = tokens.Count > 0 ? ImapProtocol.DecodeModifiedUtf7(tokens[0]) : string.Empty;
        var pattern = tokens.Count > 1 ? ImapProtocol.DecodeModifiedUtf7(tokens[1]) : "*";

        // LIST "" "" is the hierarchy-delimiter probe (RFC 3501 §6.3.8) — the answer is the delimiter alone.
        if (reference.Length == 0 && pattern.Length == 0)
        {
            await session.WriteLineAsync($"* {command} (\\Noselect) \"/\" \"\"");
            await session.OkAsync(tag, command);
            return;
        }

        var regex = PatternRegex(reference + pattern);
        foreach (var entry in await CatalogAsync(scope))
        {
            if (!regex.IsMatch(entry.Name))
            {
                continue;
            }

            var children = entry.HasChildren ? "\\HasChildren" : "\\HasNoChildren";
            await session.WriteLineAsync($"* {command} ({children}) \"/\" {ImapProtocol.QuoteMailbox(entry.Name)}");
        }

        await session.OkAsync(tag, command);
    }

    // RFC 3501 LIST wildcards: '*' matches anything, '%' anything except the hierarchy delimiter.
    private static System.Text.RegularExpressions.Regex PatternRegex(string pattern) =>
        new("^" + string.Concat(pattern.Select(c => c switch
        {
            '*' => ".*",
            '%' => "[^/]*",
            _ => System.Text.RegularExpressions.Regex.Escape(c.ToString()),
        })) + "$");

    internal static async Task StatusAsync(ImapSession session, IServiceScope scope, string tag, string arguments)
    {
        var tokens = ImapProtocol.Tokenize(arguments);
        if (tokens.Count < 1)
        {
            await session.WriteLineAsync($"{tag} BAD STATUS expects a mailbox");
            return;
        }

        var resolved = await ResolveAsync(session, scope, tokens[0]);
        if (resolved is null)
        {
            await session.WriteLineAsync($"{tag} NO no such mailbox");
            return;
        }

        var (mailbox, messages) = resolved.Value;
        var unseen = await UnseenCountAsync(scope, messages);
        await session.WriteLineAsync(
            $"* STATUS {ImapProtocol.QuoteMailbox(tokens[0])} (MESSAGES {messages.Count} UIDNEXT {mailbox.NextUid} UIDVALIDITY {mailbox.UidValidity} UNSEEN {unseen} RECENT 0)");
        await session.OkAsync(tag, "STATUS");
    }

    internal static async Task SelectAsync(ImapSession session, IServiceScope scope, string tag, string arguments, bool readOnly)
    {
        var tokens = ImapProtocol.Tokenize(arguments);
        if (tokens.Count < 1)
        {
            await session.WriteLineAsync($"{tag} BAD SELECT expects a mailbox");
            return;
        }

        var resolved = await ResolveAsync(session, scope, tokens[0]);
        if (resolved is null)
        {
            session.Selected = null;
            await session.WriteLineAsync($"{tag} NO no such mailbox");
            return;
        }

        var (mailbox, messages) = resolved.Value;
        session.Selected = new ImapSelectedMailbox(mailbox.FolderId, tokens[0], messages, ReadOnly: readOnly);

        await session.WriteLineAsync($"* {messages.Count} EXISTS");
        await session.WriteLineAsync("* 0 RECENT");
        await session.WriteLineAsync("* FLAGS (\\Seen \\Deleted)");
        await session.WriteLineAsync($"* OK [UIDVALIDITY {mailbox.UidValidity}] UIDs valid");
        await session.WriteLineAsync($"* OK [UIDNEXT {mailbox.NextUid}] predicted next UID");
        // \Seen persists per user + document (#562 slice 2) — the one storable flag; everything else stays
        // read-only until the write slice.
        // \Seen persists (slice 2). \Deleted is session-transient by design (#562) — strictly it is not
        // "permanent", but clients (MailKit measured, line: `UID STORE .. +FLAGS ()`) intersect a STORE's flags
        // with PERMANENTFLAGS and silently drop what is missing, which turns delete-then-expunge into a no-op.
        // Advertising it is what makes the normal flag-then-expunge flow work; a client that flags and
        // disconnects without EXPUNGE loses the staging, which is exactly the decided semantics.
        await session.WriteLineAsync("* OK [PERMANENTFLAGS (\\Seen \\Deleted)] seen state persists; deleted stages until EXPUNGE");
        await session.WriteLineAsync($"{tag} OK [{(readOnly ? "READ-ONLY" : "READ-WRITE")}] {(readOnly ? "EXAMINE" : "SELECT")} completed");
    }

    // ---- Catalog + resolution ------------------------------------------------------------------------

    private static async Task<List<ImapMailboxEntry>> CatalogAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;

        // Root documents: the caller's own personal repository plus the shared ones (tenant filter scopes the
        // tenant; other users' personal spaces are excluded here and by ACL).
        var roots = await db.Documents
            .Where(d => d.ParentId == null && (d.PersonalOfUserId == null || d.PersonalOfUserId == userId))
            .OrderByDescending(d => d.PersonalOfUserId == userId).ThenBy(d => d.Name)
            .ToListAsync();

        var entries = new List<ImapMailboxEntry>();
        foreach (var root in roots)
        {
            if (root.PersonalOfUserId != userId && !(await calculator.GetEffectiveRightsAsync(userId, root.Id)).CanSee)
            {
                continue;
            }

            // INBOX is the personal repository root (#562) — the name every mail client knows.
            var rootName = root.PersonalOfUserId == userId ? "INBOX" : root.Name;
            entries.Add(new ImapMailboxEntry(rootName, root.Id, HasChildren: true));
            await AddSubfoldersAsync(db, calculator, userId, root.Id, rootName, entries);
        }

        return entries;
    }

    private static async Task AddSubfoldersAsync(
        SimplArchiveDbContext db, IEffectiveRightsCalculator calculator, Guid userId, Guid parentId, string parentName, List<ImapMailboxEntry> entries)
    {
        // A mailbox is a FOLDER — a child document with no versions. Names carrying the hierarchy delimiter
        // are skipped defensively (the WebDAV gateway refuses them for the same mis-addressing reason).
        var folders = await db.Documents
            .Where(d => d.ParentId == parentId && !db.DocumentVersions.Any(v => v.DocumentId == d.Id))
            .OrderBy(d => d.Name)
            .ToListAsync();

        foreach (var folder in folders.Where(f => !f.Name.Contains('/')))
        {
            if (!(await calculator.GetEffectiveRightsAsync(userId, folder.Id)).CanSee)
            {
                continue;
            }

            var name = $"{parentName}/{folder.Name}";
            var hasSubfolders = await db.Documents.AnyAsync(d => d.ParentId == folder.Id && !db.DocumentVersions.Any(v => v.DocumentId == d.Id));
            entries.Add(new ImapMailboxEntry(name, folder.Id, hasSubfolders));
            await AddSubfoldersAsync(db, calculator, userId, folder.Id, name, entries);
        }
    }

    /// <summary>Resolves a wire mailbox name to its folder + message snapshot, assigning missing UIDs.</summary>
    internal static async Task<(ImapMailbox Mailbox, List<ImapMessageEntry> Messages)?> ResolveAsync(
        ImapSession session, IServiceScope scope, string wireName)
    {
        var name = ImapProtocol.DecodeModifiedUtf7(wireName);
        var entries = await CatalogAsync(scope);
        var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))
            // INBOX is case-insensitive by spec.
            ?? entries.FirstOrDefault(e => e.Name == "INBOX" && name.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenantId = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().TenantId!.Value;

        // The message list: child documents WITH content (a confirmed version), emails only unless the user's
        // toggle shows everything (#562). Ordered by (CreatedAt, Id) — the stable sequence-number order.
        var docs = await db.Documents
            .Where(d => d.ParentId == entry.FolderId && db.DocumentVersions.Any(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync();

        var mailbox = await db.ImapMailboxes.FirstOrDefaultAsync(m => m.FolderId == entry.FolderId);
        if (mailbox is null)
        {
            mailbox = new ImapMailbox
            {
                FolderId = entry.FolderId,
                TenantId = tenantId,
                // Any positive value works as the epoch marker; seconds-since-2020 stays well inside int and
                // differs between a purged-and-recreated folder's two lives.
                UidValidity = (int)(DateTimeOffset.UtcNow - new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds,
                NextUid = 1,
            };
            db.ImapMailboxes.Add(mailbox);
        }

        var uids = await db.ImapMessageUids.Where(u => u.FolderId == entry.FolderId).ToDictionaryAsync(u => u.DocumentId, u => u.Uid);

        var messages = new List<ImapMessageEntry>();
        foreach (var doc in docs)
        {
            var version = await CurrentVersion.ResolveAsync(db.DocumentVersions, doc.Id, doc.CurrentVersionId);
            if (version is null)
            {
                continue;
            }

            var extension = Path.GetExtension(version.ObjectKey);
            if (!session.ShowAllDocuments && !extension.Equals(".eml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!uids.TryGetValue(doc.Id, out var uid))
            {
                uid = mailbox.NextUid;
                mailbox.NextUid++;
                db.ImapMessageUids.Add(new ImapMessageUid { FolderId = entry.FolderId, DocumentId = doc.Id, TenantId = tenantId, Uid = uid });
                uids[doc.Id] = uid;
            }

            messages.Add(new ImapMessageEntry(doc.Id, uid, doc.Name, extension, version.ObjectKey, version.SizeBytes, version.CreatedAt));
        }

        await db.SaveChangesAsync();
        return (mailbox, messages.OrderBy(m => m.Uid).ToList());
    }

    /// <summary>The documents of <paramref name="messages"/> the CALLER has not seen (#562 slice 2).</summary>
    internal static async Task<int> UnseenCountAsync(IServiceScope scope, IReadOnlyList<ImapMessageEntry> messages)
    {
        var seen = await SeenSetAsync(scope, messages);
        return messages.Count(m => !seen.Contains(m.DocumentId));
    }

    /// <summary>The caller's seen-document set for one message list — the row IS the flag.</summary>
    internal static async Task<HashSet<Guid>> SeenSetAsync(IServiceScope scope, IReadOnlyList<ImapMessageEntry> messages)
    {
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        var ids = messages.Select(m => m.DocumentId).ToList();
        return (await db.ImapSeenMarks.Where(s => s.UserId == userId && ids.Contains(s.DocumentId)).Select(s => s.DocumentId).ToListAsync()).ToHashSet();
    }

    /// <summary>Upserts the caller's \Seen mark for one document; no-op when already seen.</summary>
    internal static async Task MarkSeenAsync(IServiceScope scope, Guid documentId, bool seen)
    {
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;
        var tenantId = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().TenantId!.Value;
        var existing = await db.ImapSeenMarks.FirstOrDefaultAsync(s => s.UserId == userId && s.DocumentId == documentId);
        if (seen && existing is null)
        {
            db.ImapSeenMarks.Add(new Domain.Imap.ImapSeenMark { UserId = userId, DocumentId = documentId, TenantId = tenantId, SeenAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        else if (!seen && existing is not null)
        {
            db.ImapSeenMarks.Remove(existing);
            await db.SaveChangesAsync();
        }
    }
}
