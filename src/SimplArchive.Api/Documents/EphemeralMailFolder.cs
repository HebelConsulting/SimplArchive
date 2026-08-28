using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The one answer to "is this folder ephemeral mail staging?" (#640).
/// </summary>
/// <remarks>
/// <para>
/// Ephemeral means <b>not yet in the repository</b>. That single sentence is what the sweep, the re-key and
/// IMAP's COPY all turn on, and it is why they must agree: a folder the sweep treats as staging while COPY
/// treats it as archive would produce shortcuts into storage that is about to be emptied.
/// </para>
/// <para>
/// Asked of the <c>IMAP Special</c> mask rather than the folder's name, because names are renamable and were in
/// fact renamed (<c>INBOX</c> → <c>Inbox</c>, ADR 0639) — a name-based check goes quietly blind on every space
/// provisioned before a rename, which is the failure mode this codebase keeps re-learning.
/// </para>
/// </remarks>
public static class EphemeralMailFolder
{
    /// <summary>Whether the folder is one of the staging mailboxes (it wears <c>IMAP Special</c>).</summary>
    public static async Task<bool> IsEphemeralAsync(SimplArchiveDbContext dbContext, Guid? folderId, CancellationToken cancellationToken = default)
    {
        if (folderId is not { } id)
        {
            return false; // a repository root is never staging
        }

        var maskId = await dbContext.Documents
            .Where(d => d.Id == id)
            .Select(d => dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault())
            .FirstOrDefaultAsync(cancellationToken);

        // Both masks of the staging tier: the provisioned folders and the user-created ones inside them
        // (#802). One tier, one answer — a message in Archive/Work must delete to Trash and re-key exactly
        // as one in Inbox does, or the folder a user made would silently change what deletion means.
        return maskId == WellKnownMaskIds.ImapSpecial || maskId == WellKnownMaskIds.ImapFolder;
    }

    /// <summary>
    /// The <c>Trash</c> a delete in <paramref name="folderId"/> should move to — or null when a delete there is
    /// final (#658).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null for two different reasons, and both mean "do not move": the folder IS Trash (a delete in Trash is
    /// the last one), or it is an ordinary archive folder rather than mail staging. The second is deliberate
    /// and worth stating, because the tempting generalisation is destructive: moving an ARCHIVED document into
    /// the personal mail Trash would take it out of the repository and hand it to the sweep, which empties that
    /// prefix after the retention period. A delete in an ordinary folder therefore keeps its existing meaning —
    /// soft-delete into the recycle bin, exactly as the workbench does.
    /// </para>
    /// <para>
    /// Resolved as a SIBLING under the same mailbox, so a user with several mailboxes gets their own Trash
    /// rather than somebody else's, and by the <c>IMAP Special</c> mask rather than by name for the reason the
    /// class comment gives.
    /// </para>
    /// </remarks>
    public static async Task<Guid?> TrashForDeleteAsync(
        SimplArchiveDbContext dbContext, Guid folderId, CancellationToken cancellationToken = default)
    {
        if (!await IsEphemeralAsync(dbContext, folderId, cancellationToken))
        {
            return null; // an ordinary archive folder — a delete there is a recycle-bin delete
        }

        var folder = await dbContext.Documents
            .Where(d => d.Id == folderId)
            .Select(d => new { d.Name, d.ParentId })
            .FirstOrDefaultAsync(cancellationToken);

        if (folder is null
            || folder.ParentId is not { } mailboxId
            || folder.Name == PersonalMailboxProvisioner.TrashFolderName)
        {
            return null; // already Trash: this delete is the final one
        }

        // A USER folder can be nested (Archive/Work/2026), so its parent is not the mailbox — walk up until
        // it is (#802). The loop ascends the ephemeral chain: each hop is another staging folder, and the
        // first non-ephemeral ancestor is the Mailbox the Trash lives under. Bounded by the tree depth the
        // cycle invariant already guarantees.
        while (await IsEphemeralAsync(dbContext, mailboxId, cancellationToken))
        {
            var up = await dbContext.Documents
                .Where(d => d.Id == mailboxId)
                .Select(d => d.ParentId)
                .FirstOrDefaultAsync(cancellationToken);
            if (up is not { } next)
            {
                return null; // a staging folder at a root — structurally impossible, but never loop on it
            }

            mailboxId = next;
        }

        return await dbContext.Documents
            .Where(d => d.ParentId == mailboxId
                && d.Name == PersonalMailboxProvisioner.TrashFolderName
                && dbContext.MaskVersions.Any(mv => mv.Id == d.MaskVersionId && mv.MaskId == WellKnownMaskIds.ImapSpecial))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// A name no sibling in <paramref name="targetFolderId"/> already holds.
    /// </summary>
    /// <remarks>
    /// Not defensive tidiness: two messages sharing a subject is ORDINARY in mail — a thread is a pile of
    /// "Re: the thing" — and the sibling-name invariant refuses the second one. Without this, deleting the
    /// second message of a thread fails while the first succeeds, which reads as a broken mailbox.
    /// </remarks>
    public static async Task<string> FreeNameAsync(
        SimplArchiveDbContext dbContext, Guid targetFolderId, string name, CancellationToken cancellationToken = default)
    {
        var taken = await dbContext.Documents
            .Where(d => d.ParentId == targetFolderId)
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);

        if (!taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return name;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }
}
