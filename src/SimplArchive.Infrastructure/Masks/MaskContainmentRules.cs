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
    private readonly IReadOnlyDictionary<Guid, string> _icons;
    private readonly IReadOnlySet<Guid> _userCreatable;

    private MaskContainmentRules(
        IReadOnlySet<Guid> exclusiveFolders,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> admittedChildren,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> allowedParents,
        IReadOnlySet<Guid> leafFolders,
        IReadOnlySet<Guid> folderMasks,
        IReadOnlyDictionary<Guid, string> names,
        IReadOnlyDictionary<Guid, string> icons,
        IReadOnlySet<Guid> userCreatable)
    {
        _exclusiveFolders = exclusiveFolders;
        _admittedChildren = admittedChildren;
        _allowedParents = allowedParents;
        _leafFolders = leafFolders;
        _folderMasks = folderMasks;
        _names = names;
        _icons = icons;
        _userCreatable = userCreatable;
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
            .Select(m => new { m.Id, m.IsFolderMask, m.AdmitsOnlyDeclaredChildren, m.AdmitsNoSubfolders, m.Icon, m.UserCreatable })
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
            names,
            masks.Where(m => m.Icon != null).ToDictionary(m => m.Id, m => m.Icon!),
            masks.Where(m => m.UserCreatable).Select(m => m.Id).ToHashSet());
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
        switch (Check(ownMaskId, parentMaskId))
        {
            case Refusal.None:
                return;

            case Refusal.FolderAdmitsOnly:
                throw TypedFolderContainmentException.FolderAdmitsOnly(
                    documentName, NameOf(parentMaskId!.Value), [.. AdmittedBy(parentMaskId.Value).Select(NameOf)]);

            case Refusal.ItemBelongsElsewhere:
                throw TypedFolderContainmentException.ItemBelongsIn(
                    documentName, NameOf(ownMaskId!.Value), [.. _allowedParents[ownMaskId.Value].Select(NameOf)]);

            default:
                throw TypedFolderContainmentException.FolderHoldsNoSubfolders(documentName, NameOf(parentMaskId!.Value));
        }
    }

    /// <summary>
    /// The same question without the refusal — for deciding whether to OFFER an action (#673).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked once per row when a listing is built, so it must not allocate an exception to throw away. That is
    /// why the decision below returns a reason rather than an exception, and why <see cref="Verify"/> builds
    /// the message only once it knows there is one to build.
    /// </para>
    /// <para>
    /// Sharing the decision with <see cref="Verify"/> is the point: what the client is offered and what the
    /// invariant permits are then the SAME answer rather than two that agree today. That drift is the one
    /// <c>ChildCreationPolicyAgreementTests</c> exists to catch, and this removes its possibility instead.
    /// </para>
    /// </remarks>
    public bool Allows(Guid? ownMaskId, Guid? parentMaskId) => Check(ownMaskId, parentMaskId) == Refusal.None;

    /// <summary>The masks this folder DECLARES it admits, in name order. Empty for an ordinary folder.</summary>
    /// <remarks>
    /// What a "New …" menu is built from, and deliberately narrower than "everything <see cref="Allows"/>
    /// permits": an ordinary folder permits almost every mask, and a menu listing them all would offer
    /// "New Basic Entry" beside "New folder". A declaration is an intent to hold something; mere permission is
    /// not, and only the first belongs on a menu.
    /// </remarks>
    public IReadOnlyList<Guid> AdmittedBy(Guid folderMaskId) =>
        _admittedChildren.TryGetValue(folderMaskId, out var admitted) ? admitted : [];

    /// <summary>Whether this mask types a folder, as the tenant's own model says (ADR 0653).</summary>
    public bool IsFolderMask(Guid maskId) => _folderMasks.Contains(maskId);

    /// <summary>The mask's name on its current version — what a menu entry and a refusal both read.</summary>
    public string NameOf(Guid maskId) => _names.TryGetValue(maskId, out var name) ? name : maskId.ToString();

    /// <summary>What a mask is drawn as, or <c>null</c> to leave the row on its shape default.</summary>
    /// <remarks>
    /// Answered from here because this object already holds every mask the tenant has — it is loaded once per
    /// request and shared with the invariant, so the icon costs no query at all. The class is named for
    /// containment and now carries three things that are not containment (names, folder-ness, icons); what it
    /// really holds is the tenant's mask facts, and the name is narrower than the type. Worth renaming when
    /// something else forces a pass over it, not on its own.
    /// </remarks>
    /// <summary>Whether a user may create a document wearing this mask at all (#678).</summary>
    /// <remarks>
    /// A property of the KIND of thing, asked separately from "may this caller, here" — that is rights and
    /// containment. A mask this tenant does not have reads as NOT creatable: an id nobody recognises is not a
    /// licence, and the alternative would let an unknown guid through the one gate that stops a menu offering
    /// what provisioning owns.
    /// </remarks>
    public bool IsUserCreatable(Guid maskId) => _userCreatable.Contains(maskId);

    /// <summary>Every mask in this tenant a user may create, including ones the application never shipped.</summary>
    /// <remarks>
    /// Enumerable, because a menu has to be BUILT rather than checked: the caller does not know what masks the
    /// tenant has, which is the entire point of the fact being data. Says nothing about whether any of them may
    /// live in a particular folder — ask <see cref="Allows"/> for that.
    /// </remarks>
    public IReadOnlyCollection<Guid> UserCreatableMasks => _userCreatable;

    public string? IconOf(Guid? maskId) =>
        maskId is { } id && _icons.TryGetValue(id, out var icon) ? icon : null;

    private enum Refusal
    {
        None,
        FolderAdmitsOnly,
        ItemBelongsElsewhere,
        FolderHoldsNoSubfolders,
    }

    // Three independent questions, never an if/else chain: a Section satisfies two of them at once — it is an
    // admitted child of a Notebook AND a typed folder in its own right — so treating them as alternatives would
    // let a Section live at the archive root.
    private Refusal Check(Guid? ownMaskId, Guid? parentMaskId)
    {
        // A document whose type is not DETERMINED yet is exempt: an upload creates the row (and its Pending
        // version) BEFORE the finalizer can read the bytes and classify it, so refusing here would reject every
        // .vcf/.ics before it could become a Contact/Appointment. Nothing escapes through the gap —
        // classification ends by assigning either the item mask (admitted) or Basic Entry (a real mask, so the
        // very next save is refused, which is exactly the rejection we want).
        if (ownMaskId is not { } childMaskId)
        {
            return Refusal.None;
        }

        // Does the parent admit this child? Only an EXCLUSIVE folder narrows; an ordinary one holds anything,
        // which is the permissive default every mask had before containment was modelled.
        if (parentMaskId is { } folderMaskId
            && _exclusiveFolders.Contains(folderMaskId)
            && !AdmittedBy(folderMaskId).Contains(childMaskId))
        {
            return Refusal.FolderAdmitsOnly;
        }

        // …and is this child somewhere that admits it? No rows means anywhere — the table is a restriction, so
        // absence has to read as "unrestricted" rather than as "nowhere", or every unlisted mask would be
        // unfileable.
        if (_allowedParents.TryGetValue(childMaskId, out var allowed)
            && !(parentMaskId is { } actual && allowed.Contains(actual)))
        {
            return Refusal.ItemBelongsElsewhere;
        }

        // …and if the parent holds no subfolders, is this child one? Folder-ness comes from the MASK because
        // the usual tell — does it have versions — cannot be read here: a folder and a just-delivered message
        // are both version-less at the instant this runs.
        //
        // A child that DECLARES this parent as an allowed home passes the gate (#802). "No subfolders" means
        // no ORDINARY subfolders — it was written to keep plain folders and repositories out of the staging
        // mailboxes, and a mask whose allowed-parents row names the leaf was placed by declaration, not by
        // accident. Data both ways: the gate is a flag on the parent, the exception a row on the child, and
        // neither names a folder instance — which is the trap admission-by-name set in #630.
        return _folderMasks.Contains(childMaskId) && parentMaskId is { } leafId && _leafFolders.Contains(leafId)
            && !(_allowedParents.TryGetValue(childMaskId, out var declaredHomes) && declaredHomes.Contains(leafId))
            ? Refusal.FolderHoldsNoSubfolders
            : Refusal.None;
    }
}
