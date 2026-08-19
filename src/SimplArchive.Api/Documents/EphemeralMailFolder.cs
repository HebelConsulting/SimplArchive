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

        return maskId == WellKnownMaskIds.ImapSpecial;
    }
}
