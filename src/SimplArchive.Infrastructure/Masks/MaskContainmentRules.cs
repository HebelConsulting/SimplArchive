using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Masks;

/// <summary>
/// One tenant's typed-folder containment, read from the model (#673, ADR 0655).
/// </summary>
/// <remarks>
/// <para>
/// The four facts ADR 0654 stored, loaded once and then asked in memory. Before this, the same questions were
/// answered from the static tables in <see cref="WellKnownMaskIds"/>, so containment could only ever describe
/// the families the application ships — a tenant-authored folder mask had no containment at all.
/// </para>
/// <para>
/// <b><see cref="Verify"/> is pure</b>, which is the point of it being here rather than in the DbContext. The
/// decision needs no database once the rules are loaded, so it can be exercised directly — and it is: the
/// equivalence sweep drives THIS method against the static reading over every mask pair, rather than against a
/// second implementation written to agree with it.
/// </para>
/// <para>
/// Capacity is deliberately absent. "Is there already one?" needs to count siblings, which is a query and not a
/// rule, and it stays in the DbContext beside the other invariants that read the change tracker.
/// </para>
/// </remarks>
public sealed class MaskContainmentRules
{
    private readonly IReadOnlySet<Guid> _exclusiveFolders;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> _admittedChildren;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> _allowedParents;
    private readonly IReadOnlySet<Guid> _leafFolders;
    private readonly IReadOnlySet<Guid> _folderMasks;
    private readonly IReadOnlyDictionary<Guid, string> _names;

    private MaskContainmentRules(
        IReadOnlySet<Guid> exclusiveFolders,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> admittedChildren,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> allowedParents,
        IReadOnlySet<Guid> leafFolders,
        IReadOnlySet<Guid> folderMasks,
        IReadOnlyDictionary<Guid, string> names)
    {
        _exclusiveFolders = exclusiveFolders;
        _admittedChildren = admittedChildren;
        _allowedParents = allowedParents;
        _leafFolders = leafFolders;
        _folderMasks = folderMasks;
        _names = names;
    }

    /// <summary>Reads a tenant's containment: four queries, and the caller is expected to cache the result.</summary>
    /// <remarks>
    /// Every query is explicitly scoped by <paramref name="tenantId"/> and therefore ignores the tenant filter,
    /// which is redundant here and actively wrong when no <c>ICurrentTenantAccessor.TenantId</c> is set — its
    /// predicate is <c>TenantId == null</c>, which matches nothing and would report a tenant with no rules at
    /// all. That failure would be silent and permissive: no rules reads as "anything may go anywhere".
    /// </remarks>
    public static async Task<MaskContainmentRules> LoadAsync(
        SimplArchiveDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var masks = await db.Masks.IgnoreQueryFilters(["TenantFilter"])
            .Where(m => m.TenantId == tenantId)
            .Select(m => new { m.Id, m.IsFolderMask, m.AdmitsOnlyDeclaredChildren, m.AdmitsNoSubfolders })
            .ToListAsync(cancellationToken);

        // The CURRENT version's name, so a refusal names the mask as it is called today. The static tables
        // carried their own display names, which a rename left stale — "Note Folder" outlived the rename to
        // "Notebook" everywhere but the seeder.
        var names = (await db.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenantId && v.IsCurrent)
            .Select(v => new { v.MaskId, v.Name })
            .ToListAsync(cancellationToken))
            .ToDictionary(v => v.MaskId, v => v.Name);

        var parents = await db.MaskAllowedParents.IgnoreQueryFilters(["TenantFilter"])
            .Where(p => p.TenantId == tenantId)
            .Select(p => new { p.MaskId, p.ParentMaskId })
            .ToListAsync(cancellationToken);

        var children = await db.MaskAdmittedChildren.IgnoreQueryFilters(["TenantFilter"])
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.FolderMaskId, c.ChildMaskId })
            .ToListAsync(cancellationToken);

        string NameOf(Guid maskId) => names.TryGetValue(maskId, out var name) ? name : maskId.ToString();

        // Ordered by NAME, so a refusal reads the same way on every run and in every deployment. Row order out
        // of the database is not defined, and a message that lists the same masks in a different order each
        // time is one nobody can quote in a bug report.
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Group<T>(
            IEnumerable<T> rows, Func<T, Guid> key, Func<T, Guid> value) =>
            rows.GroupBy(key).ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Guid>)[.. g.Select(value).Distinct().OrderBy(NameOf, StringComparer.Ordinal)]);

        return new MaskContainmentRules(
            masks.Where(m => m.AdmitsOnlyDeclaredChildren).Select(m => m.Id).ToHashSet(),
            Group(children, c => c.FolderMaskId, c => c.ChildMaskId),
            Group(parents, p => p.MaskId, p => p.ParentMaskId),
            masks.Where(m => m.AdmitsNoSubfolders).Select(m => m.Id).ToHashSet(),
            masks.Where(m => m.IsFolderMask).Select(m => m.Id).ToHashSet(),
            names);
    }

    /// <summary>
    /// Refuses a placement that containment forbids. Three independent questions, never an if/else chain.
    /// </summary>
    /// <param name="documentName">Named in the refusal, so the user can tell which document was rejected.</param>
    /// <param name="ownMaskId">The child's mask, or <c>null</c> when its type is not determined yet.</param>
    /// <param name="parentMaskId">The parent's mask, or <c>null</c> at a root or under a mask-less parent.</param>
    /// <remarks>
    /// A Section satisfies two of these at once — it is an admitted child of a Notebook AND a typed folder in
    /// its own right — so they are asked independently. Treating them as alternatives would let a Section live
    /// at the archive root.
    /// </remarks>
    public void Verify(string documentName, Guid? ownMaskId, Guid? parentMaskId)
    {
        // A document whose type is not DETERMINED yet is exempt from the folder's admission rule: an upload
        // creates the row (and its Pending version) BEFORE the finalizer can read the bytes and classify it, so
        // enforcing here would refuse every .vcf/.ics before it could become a Contact/Appointment. Nothing
        // escapes through the gap — classification ends by assigning either the item mask (admitted) or Basic
        // Entry (a real mask, so the very next save is refused, which is exactly the rejection we want).
        if (ownMaskId is not { } childMaskId)
        {
            return;
        }

        // Does the parent admit this child? Only an EXCLUSIVE folder narrows; an ordinary one holds anything,
        // which is the permissive default every mask had before containment was modelled.
        if (parentMaskId is { } folderMaskId && _exclusiveFolders.Contains(folderMaskId))
        {
            var admitted = _admittedChildren.TryGetValue(folderMaskId, out var list) ? list : [];
            if (!admitted.Contains(childMaskId))
            {
                throw TypedFolderContainmentException.FolderAdmitsOnly(
                    documentName, NameOf(folderMaskId), [.. admitted.Select(NameOf)]);
            }
        }

        // …and is this child somewhere that admits it? No rows means anywhere — the table is a restriction, so
        // absence has to read as "unrestricted" rather than as "nowhere", or every unlisted mask would be
        // unfileable.
        if (_allowedParents.TryGetValue(childMaskId, out var allowed)
            && !(parentMaskId is { } actual && allowed.Contains(actual)))
        {
            throw TypedFolderContainmentException.ItemBelongsIn(
                documentName, NameOf(childMaskId), [.. allowed.Select(NameOf)]);
        }

        // …and if the parent holds no subfolders, is this child one? Folder-ness comes from the MASK because
        // the usual tell — does it have versions — cannot be read here: a folder and a just-delivered message
        // are both version-less at the instant this runs.
        if (_folderMasks.Contains(childMaskId)
            && parentMaskId is { } leafId
            && _leafFolders.Contains(leafId))
        {
            throw TypedFolderContainmentException.FolderHoldsNoSubfolders(documentName, NameOf(leafId));
        }
    }

    private string NameOf(Guid maskId) => _names.TryGetValue(maskId, out var name) ? name : maskId.ToString();
}
