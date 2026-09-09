namespace SimplArchive.ModuleAbi;

/// <summary>
/// A mask an industry module contributes (ADR 0741) — the declarative half of activation: seeded into the
/// tenant idempotently, healed on upgrade, and permanent tenant data thereafter (ADR 0740: deactivation
/// removes behaviour, never masks — the documents filed under them are the tenant's).
/// </summary>
/// <param name="MaskId">The fixed cross-tenant mask id, the module's analogue of the core's well-known ids.
/// The module owns this GUID forever; activation and healing key on it.</param>
/// <param name="Name">The display name ("Medical", "Charter").</param>
/// <param name="IsFolderMask">Whether documents wearing it are folders (a pilot dossier) or items (a
/// certificate).</param>
/// <param name="IsBookable">Whether documents wearing it are bookable resources (ADR 0735) — an aircraft
/// mask says yes and inherits the core booking primitive whole.</param>
/// <param name="Fields">The mask's field definitions, in display order.</param>
/// <param name="AdmitsOnlyDeclaredChildren">Whether this FOLDER mask is exclusive (ABI 0.8, ADR 0762): its
/// "New" menu and the placement invariant admit only <paramref name="AdmittedChildren"/>. A document whose
/// type is not yet determined (a fresh upload) still lands and is classified afterwards — declare the core
/// Basic Entry (<see cref="CoreMaskIds.BasicEntry"/>) among the children if plain uploads should stay once
/// classified. False (the default) keeps today's open-by-default behaviour.</param>
/// <param name="AdmittedChildren">The masks an exclusive folder admits — module ids and/or the core ids
/// <see cref="CoreMaskIds"/> promises. Ignored unless <paramref name="AdmitsOnlyDeclaredChildren"/>; a
/// BOOKABLE mask admits its Schedule implicitly (derived from <paramref name="IsBookable"/>), so it need
/// not be declared.</param>
/// <param name="AllowedParents">Where documents wearing THIS mask may live — empty (the default) means
/// anywhere. Note a declared list refuses a mask-less parent too (an unclassified folder), so declare it
/// only for masks with a genuinely fixed home (a logbook lives in an aircraft or a dossier).</param>
public sealed record ModuleMaskSeed(
    Guid MaskId,
    string Name,
    bool IsFolderMask,
    bool IsBookable,
    IReadOnlyList<ModuleFieldSeed> Fields,
    bool AdmitsOnlyDeclaredChildren = false,
    IReadOnlyList<Guid>? AdmittedChildren = null,
    IReadOnlyList<Guid>? AllowedParents = null);

/// <summary>
/// The handful of CORE mask ids the ABI promises to a module's containment declarations (ABI 0.8) — an
/// aircraft admitting a plain Folder, a dossier admitting uploads. Kept in lockstep with the core's
/// well-known table by a core test; a module references these, never a copied GUID literal.
/// </summary>
public static class CoreMaskIds
{
    /// <summary>The plain Folder — what "New Folder" creates.</summary>
    public static readonly Guid Folder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E31");

    /// <summary>Basic Entry — what an unclassified upload becomes.</summary>
    public static readonly Guid BasicEntry = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E30");

    /// <summary>The Schedule a bookable resource holds (core ADR 0744). Admitted implicitly by
    /// <see cref="ModuleMaskSeed.IsBookable"/>; named here for AllowedParents declarations.</summary>
    public static readonly Guid Schedule = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E45");
}

/// <summary>One field of a module mask. The type vocabulary mirrors the core's field catalog.</summary>
/// <param name="Name">The field name, unique within the mask.</param>
/// <param name="DataType">One of the core's field data types by NAME ("Text", "Number", "Date", "DateTime",
/// "Boolean", "SingleSelect", "MultiSelect", "EmailAddress", "Url", "DocumentReference") — a string rather
/// than a shared enum so the ABI does not pin the core's enum ordinals into every compiled module (appending
/// a core type must never re-type a module's stored fields). "Url" (ABI 0.6) renders as a clickable link in
/// both clients' detail panes — the DABS folder's link to its source portal. "DocumentReference" (core ADR
/// 0773) holds the id of ANOTHER DOCUMENT — the typed relationship a mask names, such as the flight a lesson
/// record was flown on — validated against an existing document and rendered as one to open. WHICH documents
/// may be chosen is not part of the field: declare a <c>Proposal</c> on the mask's machine (ADR 0769) and its
/// query is the restriction, which is strictly more expressive than a mask-or-folder rule.
/// Because the vocabulary is a string, naming this on an older core is refused by name at activation —
/// no version bump, and no way for a stored field to be silently re-typed.</param>
/// <param name="IsRequired">Refused at activation when true and the mask is already worn — the same
/// protection the core's well-known heal has (a required field arriving later would invalidate documents).</param>
/// <param name="IsList">A repeatable field (the counters list on an Aircraft, ADR module-0004).</param>
public sealed record ModuleFieldSeed(
    string Name,
    string DataType,
    bool IsRequired = false,
    bool IsList = false);
