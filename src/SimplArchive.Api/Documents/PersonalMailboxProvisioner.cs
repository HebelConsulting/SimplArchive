using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Get-or-create the user's mailbox node and the standing folders inside it (#596, #617).
/// </summary>
/// <remarks>
/// <para>
/// The mailbox is created <b>on demand</b> rather than at provisioning (ADR 0630), and demand arrives from two
/// directions — a first delivered message, and the creation of IMAP credentials. The second is the one easy to
/// miss: a user who configures their mail client and finds nothing to subscribe to concludes the feature is
/// broken, when the archive is merely waiting for mail that may be days away. Creating credentials is an
/// unambiguous statement of intent, so it counts as demand in its own right.
/// </para>
/// <para>
/// Because either can fire first, every method here is idempotent and none of them assumes it owns the moment.
/// That is also why this is one service rather than a copy of the walk in LMTP and another in the IMAP layer:
/// two find-or-create paths against the same folder differ only in which one gets the rename or the heal.
/// </para>
/// </remarks>
public sealed class PersonalMailboxProvisioner
{
    /// <summary>The folder delivered mail lands in, and the one IMAP projects as <c>INBOX</c>.</summary>
    /// <remarks>
    /// <b>PascalCase, and the wire name differs.</b> These are folders in the archive tree first and mailboxes
    /// second, so they are named the way every other folder is; IMAP's <c>INBOX</c> is a protocol constant
    /// (RFC 3501 mandates the literal, case-insensitively) and is applied at the projection. The notebook set
    /// the precedent — <c>Notebook</c> in the tree, <c>NOTES</c> on the wire.
    /// </remarks>
    public const string InboxFolderName = "Inbox";

    /// <summary>The name an <c>INBOX</c> provisioned before the PascalCase rename still carries.</summary>
    public const string LegacyInboxFolderName = "INBOX";

    /// <summary>Messages the user is composing — RFC 6154 <c>\Drafts</c>.</summary>
    public const string DraftsFolderName = "Drafts";

    /// <summary>Messages the user has sent — RFC 6154 <c>\Sent</c>.</summary>
    public const string SentFolderName = "Sent";

    /// <summary>Suspected spam — RFC 6154 <c>\Junk</c>. Swept (#640).</summary>
    public const string JunkFolderName = "Junk";

    /// <summary>Discarded mail — RFC 6154 <c>\Trash</c>. Swept (#640).</summary>
    public const string TrashFolderName = "Trash";

    /// <summary>
    /// The user-organizable half of the mailbox (#802): the standing folder under which a user creates their
    /// own <c>IMAP Folder</c>s and sorts mail. Ephemeral tier like its five siblings — the mailbox answers
    /// "how do ephemeral eMails reach the repository", and for the small installation using SimplArchive AS
    /// its mailbox, this is where order lives. Projects over IMAP as <c>Archive</c> with the RFC 6154
    /// <c>\Archive</c> attribute, so a mail client's Archive button files into it natively.
    /// </summary>
    public const string EmailArchiveFolderName = "eMail-Archive";

    /// <summary>
    /// The standing mailboxes every mailbox gets, in the order a client conventionally shows them.
    /// </summary>
    /// <remarks>
    /// All six wear <see cref="WellKnownMaskIds.ImapSpecial"/>, which is what makes their contents
    /// <b>ephemeral</b> — staged under the <c>mail/</c> key prefix rather than filed in the repository
    /// (ADR 0638). Only <c>Junk</c> and <c>Trash</c> are ever swept (#640); the rest keep their mail until the
    /// user files or deletes it.
    /// </remarks>
    public static readonly IReadOnlyList<string> StandingFolderNames =
        [InboxFolderName, DraftsFolderName, SentFolderName, JunkFolderName, TrashFolderName, EmailArchiveFolderName];

    private readonly SimplArchiveDbContext _dbContext;
    private readonly PersonalRepositoryProvisioner _personalSpace;

    public PersonalMailboxProvisioner(SimplArchiveDbContext dbContext, PersonalRepositoryProvisioner personalSpace)
    {
        _dbContext = dbContext;
        _personalSpace = personalSpace;
    }

    /// <summary>The user's mailbox, creating it (and the personal space it hangs in) if it is not there yet.</summary>
    public async Task<Document> EnsureMailboxAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var personal = await _personalSpace.EnsureAsync(userId, tenantId, cancellationToken);

        var mailboxVersionIds = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenantId && v.MaskId == WellKnownMaskIds.Mailbox)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var existing = await _dbContext.Documents
            .Where(d => d.ParentId == personal.Id && d.MaskVersionId != null && mailboxVersionIds.Contains(d.MaskVersionId.Value))
            .FirstOrDefaultAsync(cancellationToken);

        // Found by its MASK, not its name, so the 2026-08-19 rename cannot orphan one — but a space
        // provisioned before it would keep the old name forever while new ones got the new one. Rename the node
        // already in hand rather than leaving the two to drift (#574).
        if (existing is { } found)
        {
            if (found.Name == PersonalRepositoryProvisioner.LegacyMyEmailsFolderName)
            {
                found.Name = PersonalRepositoryProvisioner.MyMailboxFolderName;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            // …and a mailbox provisioned when INBOX was the only standing folder is missing the other four.
            // Reached here rather than only on creation because a grow-later seed strands exactly the
            // population it is not tested against — fresh volumes have all five, upgraded ones have one (#574).
            await EnsureStandingFoldersAsync(found, tenantId, userId, cancellationToken);
            return found;
        }

        var mailbox = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = personal.Id,
            Name = PersonalRepositoryProvisioner.MyMailboxFolderName,
            MaskVersionId = mailboxVersionIds.FirstOrDefault(),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(mailbox);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await EnsureStandingFoldersAsync(mailbox, tenantId, userId, cancellationToken);
        return mailbox;
    }

    /// <summary>The six standing mailboxes, created or healed. Idempotent, and safe to call on every path.</summary>
    private async Task EnsureStandingFoldersAsync(Document mailbox, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        foreach (var name in StandingFolderNames)
        {
            await EnsureStandingFolderAsync(
                mailbox, tenantId, userId, name, WellKnownMaskIds.ImapSpecial, cancellationToken,
                // Only the inbox has a former name to answer to; the other four are new.
                legacyName: name == InboxFolderName ? LegacyInboxFolderName : null);
        }
    }

    /// <summary>
    /// A DEPARTMENT mailbox's <c>Inbox</c>, created lazily on first delivery (#703 PR 4, owner-decided
    /// 2026-08-23: Inbox only — no Junk/Trash/Sent/Drafts, the accepted trade being a second mailbox shape).
    /// </summary>
    /// <remarks>
    /// Attribution comes from the mailbox itself: a department mailbox has no owning user, and its creator —
    /// user or service account, exactly one is set — is the nearest true statement about who caused the
    /// folder to exist.
    /// </remarks>
    public async Task<Guid> EnsureInboxForMailboxAsync(Guid mailboxDocumentId, CancellationToken cancellationToken)
    {
        var mailbox = await _dbContext.Documents.SingleAsync(d => d.Id == mailboxDocumentId, cancellationToken);
        return await EnsureStandingFolderAsync(
            mailbox, mailbox.TenantId, mailbox.CreatedByUserId, InboxFolderName, WellKnownMaskIds.ImapSpecial,
            cancellationToken, createdByServiceAccountId: mailbox.CreatedByServiceAccountId);
    }

    /// <summary>The user's <c>INBOX</c>, creating the mailbox around it if need be.</summary>
    public async Task<Guid> EnsureInboxAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await EnsureStandingFolderAsync(
            await EnsureMailboxAsync(tenantId, userId, cancellationToken),
            tenantId, userId, InboxFolderName, WellKnownMaskIds.ImapSpecial, cancellationToken,
            legacyName: LegacyInboxFolderName);

    /// <summary>
    /// The user's notebook — the folder Apple Notes creates as <c>NOTES</c> and fills with notes (#596).
    /// </summary>
    /// <remarks>
    /// It lives under the mailbox rather than loose in the personal space because a Notebook only means
    /// anything through a notes client speaking IMAP; the containment rule in
    /// <see cref="WellKnownMaskIds.TypedFolderRules"/> is what makes that placement the only one.
    /// </remarks>
    public async Task<Guid> EnsureNotebookAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await EnsureStandingFolderAsync(
            await EnsureMailboxAsync(tenantId, userId, cancellationToken),
            tenantId, userId, PersonalRepositoryProvisioner.NotebookFolderName, WellKnownMaskIds.Notebook, cancellationToken);

    // One find-or-create for both standing folders. They differ only in name and mask, so a second copy would
    // be a place for the heal below to exist in one and not the other.
    // Takes the mailbox rather than resolving it, so EnsureMailboxAsync can provision the standing set without
    // re-entering itself once per folder.
    private async Task<Guid> EnsureStandingFolderAsync(
        Document mailbox, Guid tenantId, Guid? userId, string name, Guid maskId, CancellationToken cancellationToken,
        string? legacyName = null, Guid? createdByServiceAccountId = null)
    {
        var maskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, maskId, cancellationToken);

        var existing = await _dbContext.Documents
            .Where(d => d.ParentId == mailbox.Id && (d.Name == name || (legacyName != null && d.Name == legacyName)))
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } found)
        {
            // The PascalCase rename, applied to the node already in hand. Renaming rather than creating a
            // second folder: the old INBOX holds the user's delivered mail, and a fresh empty one beside it
            // would be an inbox that silently lost everything.
            if (legacyName is not null && found.Name == legacyName)
            {
                found.Name = name;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            // Heal a folder created before its mask existed. The mask is what marks an INBOX EPHEMERAL, so a
            // maskless one is not merely untyped — it is indistinguishable from archive content to anything
            // keying off the mask, the sweep included. A grow-only seed never revisits it, so the heal happens
            // where the node is already in hand (#574).
            if (found.MaskVersionId is null && maskVersionId is not null)
            {
                found.MaskVersionId = maskVersionId;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return found.Id;
        }

        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = mailbox.Id,
            Name = name,
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(folder);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return folder.Id;
    }
}
