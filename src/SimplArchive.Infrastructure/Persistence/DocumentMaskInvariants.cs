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
        // FIRST, because everything below asks which mask the document wears and the answer must be settled.
        await AssignDefaultMaskAsync(dbContext, document, cancellationToken);

        var maskId = await MaskIdOfAsync(dbContext, document.MaskVersionId, cancellationToken);

        await EnforceStructuralMaskImmutableAsync(dbContext, document, maskId, cancellationToken);
        await EnforceRepositoryLockstepAsync(dbContext, document, maskId, cancellationToken);
    }

    /// <summary>
    /// Every document wears a mask; a caller that did not choose one gets the default for its shape (#1240).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Basic Entry where the document has content, Folder where it has none.</b> That keys the default off
    /// what the document IS rather than off which code path made it, which matters because the paths disagreed:
    /// of the four that produced a maskless document, only two were a user choosing — the others were the
    /// importer's transient state before phase D, and the finaliser's fallback when staged index data failed to
    /// validate. Neither of those is a decision anyone made, and both now get a sensible type instead of a null.
    /// </para>
    /// <para>
    /// <b>The ChangeTracker is consulted, not just the database, and that is the whole subtlety.</b>
    /// <c>DocumentFinalizer</c> adds a document and its first <see cref="DocumentVersion"/> in the SAME
    /// SaveChanges. Asked as a query, "does it have versions?" answers <i>false</i> for a content document —
    /// the version row is not committed yet — and it would be typed as a Folder, which is exactly wrong. Added
    /// entries have to be counted too.
    /// </para>
    /// <para>
    /// Runs in <c>SaveChanges</c> for the same reason as every other document invariant: a document is created
    /// by the workbench, the intray, import, WebDAV, CalDAV, IMAP, LMTP and provisioning, and a default applied
    /// in one of them is a default the other seven skip.
    /// </para>
    /// </remarks>
    private static async Task AssignDefaultMaskAsync(
        SimplArchiveDbContext dbContext, Document document, CancellationToken cancellationToken)
    {
        if (document.MaskVersionId != Guid.Empty)
        {
            return;
        }

        var hasContent =
            dbContext.ChangeTracker.Entries<DocumentVersion>()
                .Any(e => e.State is EntityState.Added && e.Entity.DocumentId == document.Id)
            || await dbContext.DocumentVersions.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(v => v.DocumentId == document.Id, cancellationToken);

        var maskId = hasContent ? WellKnownMaskIds.BasicEntry : WellKnownMaskIds.Folder;

        // Resolved per tenant: the well-known ids are shared, their CURRENT versions are not (ADR 0198).
        var versionId = await dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == document.TenantId && v.MaskId == maskId && v.IsCurrent)
            .Select(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken);

        document.MaskVersionId = versionId != Guid.Empty
            ? versionId
            : SeedDefaultMask(dbContext, document.TenantId, maskId);
    }

    /// <summary>
    /// Creates the tenant's missing default mask IN THIS SAVE, so a document never has to go untyped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This should never fire.</b> The well-known masks are seeded when a tenant is provisioned and healed at
    /// startup (ADR 0757), so a live tenant has them. It exists because the cost of being wrong is asymmetric:
    /// if the premise holds this is dead code, and if it does not, the alternative was refusing every write for
    /// that tenant — the shape that boot-crashed the v0.13→v0.14 upgrade when a seed refusal met real data.
    /// </para>
    /// <para>
    /// <b>It adds rows rather than calling the seeder</b>, which calls <c>SaveChanges</c> seven times — from
    /// inside <c>SaveChanges</c> that is re-entrant. Adding to the ChangeTracker puts the mask in the SAME
    /// transaction as the document that needs it, so either both land or neither does, and EF orders the insert
    /// behind the foreign key by itself.
    /// </para>
    /// <para>
    /// <see cref="MaskVersion.VersionNumber"/> and <see cref="MaskVersion.IsCurrent"/> are deliberately not set:
    /// <c>PrepareMaskVersionsAsync</c> runs LATER in the same pipeline and owns that numbering. Setting them
    /// here would be the manual assignment the context's own rule forbids.
    /// </para>
    /// </remarks>
    private static Guid SeedDefaultMask(SimplArchiveDbContext dbContext, Guid tenantId, Guid maskId)
    {
        // A mask already queued by an earlier document in this same save — two new documents in one
        // transaction must not each add their own copy of it.
        var pending = dbContext.ChangeTracker.Entries<MaskVersion>()
            .FirstOrDefault(e => e.State is EntityState.Added
                && e.Entity.TenantId == tenantId
                && e.Entity.MaskId == maskId);
        if (pending is not null)
        {
            return pending.Entity.Id;
        }

        var maskExists = dbContext.ChangeTracker.Entries<Mask>()
            .Any(e => e.State is EntityState.Added && e.Entity.TenantId == tenantId && e.Entity.Id == maskId)
            || dbContext.Masks.IgnoreQueryFilters(["TenantFilter"])
                .Any(m => m.TenantId == tenantId && m.Id == maskId);

        if (!maskExists)
        {
            dbContext.Masks.Add(new Mask
            {
                Id = maskId,
                TenantId = tenantId,
                CreatedAt = DateTimeOffset.UtcNow,
                IsFolderMask = maskId == WellKnownMaskIds.Folder,
            });
        }

        var version = new MaskVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaskId = maskId,
            Name = maskId == WellKnownMaskIds.Folder ? "Folder" : "Basic Entry",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.MaskVersions.Add(version);
        return version.Id;
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
        var originalVersionId = entry.Property(d => d.MaskVersionId).OriginalValue;
        if (originalVersionId == Guid.Empty)
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
