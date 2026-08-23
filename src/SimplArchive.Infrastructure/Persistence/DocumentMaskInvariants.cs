using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// The invariants about which mask a document may wear: repository/mask lockstep, and the folder types that
/// cannot be changed once set.
/// </summary>
/// <remarks>
/// Extracted from <c>SimplArchiveDbContext</c> when the second rule arrived — the context is on the standing
/// debt list, and "which mask is legal here" is a responsibility with two members rather than a line in a
/// method that already had five. Driven from <c>SaveChanges</c> for the usual reason: a mask is assigned by the
/// workbench, the intray, import, WebDAV, the personal-space heal and provisioning, and a check in one of them
/// is a check the others skip.
/// </remarks>
public static class DocumentMaskInvariants
{
    public static async Task EnforceAsync(
        SimplArchiveDbContext dbContext, Document document, CancellationToken cancellationToken)
    {
        var maskId = await MaskIdOfAsync(dbContext, document.MaskVersionId, cancellationToken);

        await EnforceStructuralMaskImmutableAsync(dbContext, document, maskId, cancellationToken);
        await EnforceRepositoryLockstepAsync(dbContext, document, maskId, cancellationToken);
    }

    /// <summary>
    /// A folder wearing a structural mask keeps it (ADR 0685).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The direction matters and is the whole subtlety: this refuses changing AWAY from such a mask, never
    /// having one. Provisioning and the personal-space heal assign these masks to maskless folders, and a
    /// restamp moves a folder off plain Folder — a rule reading "wears a structural mask ⇒ refuse" would break
    /// the very paths that create them.
    /// </para>
    /// <para>
    /// Compared by MASK, not by mask VERSION: publishing a new version of the Mailbox mask re-points every
    /// mailbox at it, and that is a mask edit, not a re-type.
    /// </para>
    /// </remarks>
    private static async Task EnforceStructuralMaskImmutableAsync(
        SimplArchiveDbContext dbContext, Document document, Guid? newMaskId, CancellationToken cancellationToken)
    {
        var entry = dbContext.Entry(document);
        if (entry.State != EntityState.Modified || !entry.Property(d => d.MaskVersionId).IsModified)
        {
            return;
        }

        // Null before ⇒ this is an assignment, which is always allowed: an untyped folder becoming a Mailbox is
        // exactly what the heal does.
        if (entry.Property(d => d.MaskVersionId).OriginalValue is not { } originalVersionId)
        {
            return;
        }

        var originalMaskId = await MaskIdOfAsync(dbContext, originalVersionId, cancellationToken);
        if (originalMaskId is not { } original
            || !WellKnownMaskIds.ImmutableStructuralMasks.Contains(original)
            || newMaskId == original)
        {
            return;
        }

        // The name comes from the version the document is LEAVING, so a renamed mask produces the right message
        // rather than one hardcoded here — the same reasoning the containment invariant uses.
        var maskName = await dbContext.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.Id == originalVersionId)
            .Select(v => v.Name)
            .SingleOrDefaultAsync(cancellationToken);

        throw StructuralMaskImmutableException.CannotChange(document.Name, maskName ?? "typed folder");
    }

    // A root that acquires a parent has stopped being a repository — which is a LEGITIMATE operation (a
    // bulk move with the manage-repositories right does exactly this). So lockstep is MAINTAINED here, not
    // vetoed: refusing the move would block a supported action to protect a fact we can simply keep true.
    //
    // Doing it at the single enforcement point rather than in the move endpoint is the whole reason this
    // lives in SaveChanges: every path — bulk move, WebDAV, import, a future one — inherits it without
    // having to remember. The same reasoning as the MaskVersion auto-numbering.
    private static async Task EnforceRepositoryLockstepAsync(
        SimplArchiveDbContext dbContext, Document document, Guid? maskId, CancellationToken cancellationToken)
    {
        if (maskId == WellKnownMaskIds.Repository && document.ParentId is not null)
        {
            var folderVersionId = await dbContext.MaskVersions.IgnoreQueryFilters()
                .Where(v => v.TenantId == document.TenantId
                    && v.MaskId == WellKnownMaskIds.Folder
                    && v.IsCurrent)
                .Select(v => (Guid?)v.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (folderVersionId is { } folderVersion)
            {
                document.MaskVersionId = folderVersion;
            }
        }

        // The other direction is deliberately NOT enforced as a throw. A root is created mask-less by several
        // paths and stamped afterwards (the upload flow does exactly this, which is why typed-folder
        // containment exempts a document whose type is not yet determined), so refusing a mask-less root here
        // would break creation rather than protect anything. The backfill and the creating endpoints put the
        // mask on; this half stops it being contradicted.
        if (maskId == WellKnownMaskIds.UserFolder && document.ParentId is not null)
        {
            throw new InvalidOperationException(
                $"'{document.Name}' wears the User Folder mask but has a parent. A personal space is a root "
                + "document (ADR 0590).");
        }
    }

    private static async Task<Guid?> MaskIdOfAsync(
        SimplArchiveDbContext dbContext, Guid? maskVersionId, CancellationToken cancellationToken) =>
        maskVersionId is not { } id
            ? null
            : await dbContext.MaskVersions.IgnoreQueryFilters()
                .Where(v => v.Id == id)
                .Select(v => (Guid?)v.MaskId)
                .SingleOrDefaultAsync(cancellationToken);
}
