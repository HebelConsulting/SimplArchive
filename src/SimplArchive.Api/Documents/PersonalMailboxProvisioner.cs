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
    /// <summary>The one standing folder every mailbox gets, and the name every mail client knows.</summary>
    public const string InboxFolderName = "INBOX";

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
        return mailbox;
    }

    /// <summary>The user's <c>INBOX</c>, creating the mailbox around it if need be.</summary>
    public async Task<Guid> EnsureInboxAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await EnsureStandingFolderAsync(
            tenantId, userId, InboxFolderName, WellKnownMaskIds.ImapSpecial, cancellationToken);

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
            tenantId, userId, PersonalRepositoryProvisioner.NotebookFolderName, WellKnownMaskIds.Notebook, cancellationToken);

    // One find-or-create for both standing folders. They differ only in name and mask, so a second copy would
    // be a place for the heal below to exist in one and not the other.
    private async Task<Guid> EnsureStandingFolderAsync(
        Guid tenantId, Guid userId, string name, Guid maskId, CancellationToken cancellationToken)
    {
        var mailbox = await EnsureMailboxAsync(tenantId, userId, cancellationToken);

        var maskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, maskId, cancellationToken);

        var existing = await _dbContext.Documents
            .Where(d => d.ParentId == mailbox.Id && d.Name == name)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } found)
        {
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
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(folder);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return folder.Id;
    }
}
