using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Imap;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Imap;

/// <summary>One mailbox in the catalog: its wire name and the folder Document behind it.</summary>
/// <param name="AcceptsChildren">
/// True only for the notebook tree, where CREATE is honoured (#596). Everything else refuses CREATE, and the
/// LIST attributes now say so (#792): advertising a capability the wire denies — or holding one it never
/// advertises — are the same fault in opposite directions.
/// </param>
internal sealed record ImapMailboxEntry(string Name, Guid FolderId, bool HasChildren, bool AcceptsChildren = false);

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

            // RFC 3348 CHILDREN attributes, now under an advertised contract (#792) — and \Noinferiors for a
            // LEAF outside the notebook tree, where CREATE is refused. Only for leaves: \Noinferiors claims no
            // child can ever exist (RFC 3501 §7.2.2), which would be a lie on a read-only folder that HAS
            // children. There is no LIST attribute for "has children but takes no more"; that limit is
            // accepted rather than mis-stated.
            var children = entry.HasChildren ? "\\HasChildren" : "\\HasNoChildren";
            if (!entry.AcceptsChildren && !entry.HasChildren)
            {
                children = "\\Noinferiors " + children;
            }

            // RFC 6154 SPECIAL-USE. Clients find Drafts/Sent/Junk/Trash by ATTRIBUTE, not by name — a client
            // told nothing will helpfully create its own "Sent Messages" beside ours, and then the user has
            // two. INBOX needs none: RFC 3501 makes that name itself the special case.
            var special = SpecialUseAttribute(entry.Name);
            await session.WriteLineAsync($"* {command} ({children}{special}) \"/\" {ImapProtocol.QuoteMailbox(entry.Name)}");
        }

        await session.OkAsync(tag, command);
    }

    /// <summary>The RFC 6154 use attribute for a standing mailbox, or empty for an ordinary folder.</summary>
    /// <remarks>
    /// Keyed on the WIRE name, which is what the client sees and what <see cref="StandingMailboxesAsync"/>
    /// already resolved from the mask — so a user folder that happens to be called "Sent" somewhere else in
    /// the archive cannot claim the attribute, because it never reaches the root as that name.
    /// </remarks>
    private static string SpecialUseAttribute(string wireName) => wireName switch
    {
        Documents.PersonalMailboxProvisioner.DraftsFolderName => " \\Drafts",
        Documents.PersonalMailboxProvisioner.SentFolderName => " \\Sent",
        Documents.PersonalMailboxProvisioner.JunkFolderName => " \\Junk",
        Documents.PersonalMailboxProvisioner.TrashFolderName => " \\Trash",

        // RFC 6154 \Archive: the attribute a client's Archive button files by. Keyed on the WIRE name, like
        // the rest — the workbench calls the folder eMail-Archive, the wire calls it Archive (#802).
        "Archive" => " \\Archive",
        _ => string.Empty,
    };

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

        // RFC 3501 §6.3.10: echo only the requested items (a bare mailbox with no list gets them all —
        // lenient, since we know clients that omit it). UNSEEN is the only one that costs a query, so it
        // is computed only when asked for.
        HashSet<string> requested = tokens.Count > 1
            ? tokens[1].Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToUpperInvariant()).ToHashSet()
            : ["MESSAGES", "UIDNEXT", "UIDVALIDITY", "UNSEEN", "RECENT"];
        var items = new List<string>();
        if (requested.Contains("MESSAGES"))
        {
            items.Add($"MESSAGES {messages.Count}");
        }

        if (requested.Contains("UIDNEXT"))
        {
            items.Add($"UIDNEXT {mailbox.NextUid}");
        }

        if (requested.Contains("UIDVALIDITY"))
        {
            items.Add($"UIDVALIDITY {mailbox.UidValidity}");
        }

        if (requested.Contains("UNSEEN"))
        {
            items.Add($"UNSEEN {await UnseenCountAsync(scope, messages)}");
        }

        if (requested.Contains("RECENT"))
        {
            items.Add("RECENT 0");
        }

        // RFC 8474 §5 adds MAILBOXID to STATUS — the way a client learns a mailbox's identity WITHOUT selecting
        // it, which is what makes "did this folder get renamed, or replaced?" answerable during a LIST sweep.
        // Deliberately absent from the default set above: RFC 3501 §6.3.10 says STATUS echoes what was asked
        // for, and a client that omits the list predates OBJECTID and would not know what to do with it.
        if (requested.Contains("MAILBOXID"))
        {
            items.Add($"MAILBOXID ({ImapObjectId.ForMailbox(mailbox.FolderId)})");
        }

        await session.WriteLineAsync(
            $"* STATUS {ImapProtocol.QuoteMailbox(tokens[0])} ({string.Join(' ', items)})");
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
        // RFC 8474 §5: the mailbox's stable identity, which UIDVALIDITY is NOT — that is a cache-invalidation
        // counter. Renaming a folder in the workbench keeps this id, so a client re-labels rather than
        // re-downloading (#780).
        await session.WriteLineAsync($"* OK [MAILBOXID ({ImapObjectId.ForMailbox(mailbox.FolderId)})] mailbox identity");
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

        // The catalog's own source, so an administrator raises exactly this walk rather than every IMAP line.
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SimplArchive.Api.Imap");

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

            // INBOX is the personal repository root (#562) — the name every mail client knows. The notebook
            // projects as a ROOT-level mailbox instead, where Apple Notes discovers it — one folder, two
            // projections, so it is skipped from the ordinary walk below.
            //
            // It is a GRANDCHILD now (#596): the notebook lives under the mailbox node, not loose in the
            // personal space, so finding it means walking Personal → My Mailbox → Notebook. Both hops go by
            // MASK rather than by name — the folders were renamed on 2026-08-19 and a name-based walk would
            // have gone quietly blind on every space provisioned before that.
            // The personal space projects under its OWN name now (#596). `INBOX` used to mean this root — it was
            // written before mail was delivered anywhere, when "the name every mail client knows" was the whole
            // of the reasoning. Once LMTP started filing into Personal/My Mailbox/Inbox, that made the client's
            // INBOX a folder that structurally CANNOT hold a message (the first level admits only the
            // provisioned folders, #634), while the mail sat two levels down under another name. A mail client
            // showing an empty inbox for an account that is receiving mail is the worst kind of wrong: nothing
            // errors, and the logs of a working and a broken account are identical.
            var rootName = root.Name;
            entries.Add(new ImapMailboxEntry(rootName, root.Id, HasChildren: true));

            Guid? notesFolderId = null;
            if (root.PersonalOfUserId == userId)
            {
                notesFolderId = await db.Documents
                    .Where(d => db.Documents.Any(m =>
                            m.Id == d.ParentId
                            && m.ParentId == root.Id
                            && db.MaskVersions.Any(v => v.Id == m.MaskVersionId && v.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.Mailbox))
                        && db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.Notebook))
                    .Select(d => (Guid?)d.Id)
                    .FirstOrDefaultAsync();
                if (notesFolderId is { } nid)
                {
                    // The mailbox stays literally "Notes" though the folder is now called Notebook: that name
                    // is Apple's convention for where notes live, and an account that already works finds the
                    // mailbox by it. The rename stops at the wire.
                    //
                    // Its sections are then walked from here, so they surface as Notes/Work/2026. Before
                    // sections existed this said HasChildren: false and the subtree was never visited — which
                    // is exactly why Apple Notes could see no subfolders.
                    var hasSections = await HasSubfoldersAsync(db, nid);
                    entries.Add(new ImapMailboxEntry("Notes", nid, hasSections, AcceptsChildren: true));
                    await AddSubfoldersAsync(db, calculator, logger, userId, nid, "Notes", entries, acceptsChildren: true);
                }
            }

            // The five standing mailboxes project at the ROOT, where a mail client looks for them — `Inbox` as
            // the protocol's `INBOX`, the rest under their own names, which are already the RFC 6154
            // conventions. One folder, two projections, exactly as the notebook has: the workbench shows them
            // inside Personal/My Mailbox, IMAP shows them at the top.
            var standingIds = new HashSet<Guid>();
            if (notesFolderId is { } notes)
            {
                standingIds.Add(notes);
            }

            if (root.PersonalOfUserId == userId)
            {
                foreach (var (folderId, wireName) in await StandingMailboxesAsync(db, root.Id))
                {
                    standingIds.Add(folderId);

                    // Only the archive takes user folders (#802) — the attribute set and the CREATE handler
                    // must agree, and both read this flag.
                    var takesFolders = wireName == "Archive";
                    entries.Add(new ImapMailboxEntry(wireName, folderId, await HasSubfoldersAsync(db, folderId), takesFolders));
                    await AddSubfoldersAsync(db, calculator, logger, userId, folderId, wireName, entries, acceptsChildren: takesFolders);
                }
            }

            await AddSubfoldersAsync(db, calculator, logger, userId, root.Id, rootName, entries, standingIds);
        }

        return entries;
    }

    /// <summary>
    /// The mailbox's standing folders as (id, wire name) — `Inbox` becomes `INBOX`, the rest keep their names.
    /// </summary>
    /// <remarks>
    /// Found by MASK and then by name: the mask says which folders are the ephemeral staging mailboxes, and the
    /// name says which of the five a given one is. Walking by name alone would go blind on a space provisioned
    /// before the PascalCase rename; walking by mask alone cannot tell `Junk` from `Trash`.
    /// </remarks>
    private static async Task<List<(Guid Id, string WireName)>> StandingMailboxesAsync(SimplArchiveDbContext db, Guid personalRootId)
    {
        var folders = await db.Documents
            .Where(d => db.Documents.Any(m =>
                    m.Id == d.ParentId
                    && m.ParentId == personalRootId
                    && db.MaskVersions.Any(v => v.Id == m.MaskVersionId && v.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.Mailbox))
                && db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.ImapSpecial))
            .Select(d => new { d.Id, d.Name })
            .ToListAsync();

        // Ordered by the standing list rather than alphabetically, so INBOX leads — and an unrecognised
        // IMAP Special folder is left to the ordinary walk rather than silently promoted to the root.
        return Documents.PersonalMailboxProvisioner.StandingFolderNames
            .Select(name => (Name: name, Match: folders.FirstOrDefault(f => f.Name == name)))
            .Where(x => x.Match is not null)
            .Select(x => (x.Match!.Id, WireName: x.Name switch
            {
                Documents.PersonalMailboxProvisioner.InboxFolderName => "INBOX",

                // The workbench name says what the folder is FOR; the wire name is the one every mail client's
                // archive gesture already knows (#802). Same one-folder-two-projections rule as Notebook/Notes.
                Documents.PersonalMailboxProvisioner.EmailArchiveFolderName => "Archive",
                _ => x.Name,
            }))
            .ToList();
    }

    private static async Task AddSubfoldersAsync(
        SimplArchiveDbContext db, IEffectiveRightsCalculator calculator, ILogger logger, Guid userId, Guid parentId, string parentName, List<ImapMailboxEntry> entries, IReadOnlySet<Guid>? skipFolderIds = null, IReadOnlySet<Guid>? ancestors = null, bool acceptsChildren = false)
    {
        // The folders on the path to here, so a reference pointing back up one stops instead of recursing for
        // ever. It must be the ANCESTOR CHAIN rather than a global visited-set: the same folder legitimately
        // appears in more than one place (that is what a reference IS), and a global set would silently drop
        // every appearance after the first.
        var path = ancestors is null ? new HashSet<Guid> { parentId } : new HashSet<Guid>(ancestors) { parentId };

        // A mailbox is a FOLDER — a child document with no versions. Names carrying the hierarchy delimiter
        // are skipped defensively (the WebDAV gateway refuses them for the same mis-addressing reason).
        var folders = await db.Documents
            .Where(d => d.ParentId == parentId && !db.DocumentVersions.Any(v => v.DocumentId == d.Id))
            .OrderBy(d => d.Name)
            .ToListAsync();

        // …and the folders REFERENCED into this one (ADR 0627). A reference is another appearance of a folder,
        // so it projects as a mailbox exactly like a child does — without it, a filing destination a user put
        // in their personal space is invisible to their mail client, which is the whole point of Goal 1(b).
        // The clients already show these (DocumentReferencesController lists them alongside the children);
        // this brings IMAP into line. The soft-delete query filter drops references to deleted targets.
        var referencedIds = await db.DocumentReferences
            .Where(r => r.ParentFolderId == parentId)
            .Select(r => r.TargetDocumentId)
            .ToListAsync();

        var referenced = await db.Documents
            .Where(d => referencedIds.Contains(d.Id) && !db.DocumentVersions.Any(v => v.DocumentId == d.Id))
            .OrderBy(d => d.Name)
            .ToListAsync();

        foreach (var folder in folders.Concat(referenced).Where(f => !f.Name.Contains('/') && skipFolderIds?.Contains(f.Id) != true))
        {
            // A reference back to somewhere on our own path, or to this folder's own parent chain. Following it
            // would produce Personal/My Mailbox/Personal/My Mailbox/… until the client or the stack gave up.
            //
            // Skipping is silent to the client — the mailbox is simply absent, and nothing distinguishes that
            // from "there was never anything there", which is the shape ADR 0626 exists to forbid. So it says
            // so, and names the switch.
            if (path.Contains(folder.Id))
            {
                logger.LogWarning(
                    "IMAP catalog: reference to {FolderName} under {ParentName} points back into its own path and "
                    + "is OMITTED from the mailbox list; the client sees no such mailbox and cannot tell it was "
                    + "skipped. Set Serilog:MinimumLevel:Override:SimplArchive.Api.Imap to Trace for the walk",
                    folder.Name, parentName);
                continue;
            }

            if (!(await calculator.GetEffectiveRightsAsync(userId, folder.Id)).CanSee)
            {
                continue;
            }

            var name = $"{parentName}/{folder.Name}";

            // A referenced folder whose name matches a real child would emit the SAME wire name twice, and a
            // mail client resolving one of them gets whichever it saw last. Names must be unique in the
            // catalog, so the first appearance wins — deterministic because children are walked before
            // references.
            if (entries.Any(e => e.Name == name))
            {
                logger.LogWarning(
                    "IMAP catalog: {Name} is claimed by more than one folder (a child and a reference share a "
                    + "name); the LATER one is OMITTED and its messages are unreachable over IMAP. Rename one, "
                    + "or set Serilog:MinimumLevel:Override:SimplArchive.Api.Imap to Trace for the walk",
                    name);
                continue;
            }

            entries.Add(new ImapMailboxEntry(name, folder.Id, await HasSubfoldersAsync(db, folder.Id), acceptsChildren));
            // The skip PROPAGATES rather than stopping at the first level: the notebook it names is a
            // grandchild of the personal root (#596), so dropping it here would list it twice — once as the
            // root-level `Notes` Apple Notes expects, and again as `INBOX/My Mailbox/Notebook`.
            await AddSubfoldersAsync(db, calculator, logger, userId, folder.Id, name, entries, skipFolderIds, path, acceptsChildren);
        }
    }

    // "Is there anything below this that is itself a mailbox?" — a child folder OR a folder referenced into it,
    // since both now project. Answering with children alone was what made \HasNoChildren a lie for a folder
    // holding only references, and a client that trusts the flag never looks inside.
    private static async Task<bool> HasSubfoldersAsync(SimplArchiveDbContext db, Guid folderId) =>
        await db.Documents.AnyAsync(d => d.ParentId == folderId && !db.DocumentVersions.Any(v => v.DocumentId == d.Id))
        || await db.DocumentReferences
            .Where(r => r.ParentFolderId == folderId)
            .AnyAsync(r => db.Documents.Any(d => d.Id == r.TargetDocumentId && !db.DocumentVersions.Any(v => v.DocumentId == d.Id)));

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
            .ToListAsync();

        // …and the documents REFERENCED into this folder. A reference is another appearance of a document, so
        // it projects as a message exactly as a child does. The mailbox walk has taken referenced FOLDERS since
        // #596; leaving documents out meant a mail client listed a folder's children and silently omitted
        // everything a user had filed there by reference — reported as "only PDFs are shown, not document
        // links" (#766).
        var referencedIds = await db.DocumentReferences
            .Where(r => r.ParentFolderId == entry.FolderId)
            .Select(r => r.TargetDocumentId)
            .ToListAsync();

        var referenced = await db.Documents
            .Where(d => referencedIds.Contains(d.Id)
                // Not also a child here: the UID table is keyed by (folder, document), so one document
                // appearing twice in a mailbox would carry the SAME uid twice, which no client can hold.
                && d.ParentId != entry.FolderId
                && db.DocumentVersions.Any(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed))
            .ToListAsync();

        if (referenced.Count > 0)
        {
            // A referenced document lives somewhere ELSE, so its rights are its own — inherited from its real
            // parent, not from the folder it is referenced into. Children can be taken on the folder's own
            // CanSee, which the catalog already checked; these cannot, and serving one unchecked would hand a
            // mail client a document its owner never shared.
            var calculator = scope.ServiceProvider.GetRequiredService<IEffectiveRightsCalculator>();
            var visible = new List<Document>();
            foreach (var candidate in referenced)
            {
                if ((await calculator.GetEffectiveRightsAsync(session.UserId, candidate.Id)).CanSee)
                {
                    visible.Add(candidate);
                }
            }

            docs.AddRange(visible);
        }

        // Ordered by (CreatedAt, Id) — the stable sequence-number order — AFTER the two sources are joined, so
        // a referenced document sits where its age puts it rather than after every child.
        docs = docs.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToList();

        var mailbox = await db.ImapMailboxes.FirstOrDefaultAsync(m => m.FolderId == entry.FolderId);
        if (mailbox is null)
        {
            mailbox = new ImapMailbox
            {
                FolderId = entry.FolderId,
                TenantId = tenantId,
                // Derived from the FOLDER id, not the clock (#781). UIDVALIDITY's one job is to change exactly
                // when previously-issued UIDs stop being valid — which happens when a folder is purged and
                // recreated, and a recreated folder has a new GUID. The old seconds-since-2020 value also
                // changed on occasions when the UIDs were still perfectly valid (any row recreation, the demo
                // reseed included), which is a spurious full resync. With the demo's ids deterministic (#781),
                // the reseeded folder keeps its id and therefore its UIDVALIDITY — and its UIDs re-derive over
                // the same set in the same order, which is what makes keeping it honest. A real folder's
                // recreation mints a fresh GUID, so this still changes between its two lives.
                UidValidity = UidValidityFor(entry.FolderId),
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

        // The catalog's own source, so an administrator raises exactly this walk rather than every IMAP line.
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SimplArchive.Api.Imap");
        var ids = messages.Select(m => m.DocumentId).ToList();
        return (await db.ImapSeenMarks.Where(s => s.UserId == userId && ids.Contains(s.DocumentId)).Select(s => s.DocumentId).ToListAsync()).ToHashSet();
    }

    /// <summary>Upserts the caller's \Seen mark for one document; no-op when already seen.</summary>
    internal static async Task MarkSeenAsync(IServiceScope scope, Guid documentId, bool seen)
    {
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var userId = scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>().UserId!.Value;

        // The catalog's own source, so an administrator raises exactly this walk rather than every IMAP line.
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SimplArchive.Api.Imap");
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

    /// <summary>A stable, positive UIDVALIDITY from the folder's own identity (#781; see the assignment above).</summary>
    internal static int UidValidityFor(Guid folderId)
    {
        var value = BitConverter.ToInt32(folderId.ToByteArray(), 0) & int.MaxValue;
        return value == 0 ? 1 : value;
    }

    // ---- Mailbox lifecycle: CREATE / DELETE / RENAME -------------------------------------------------
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
