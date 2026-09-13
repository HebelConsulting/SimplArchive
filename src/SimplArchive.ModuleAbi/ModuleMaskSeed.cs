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
/// <param name="RepresentsPrincipalField">The name of THIS mask's own field that identifies the PERSON a
/// document wearing it stands for (ABI 0.13, core ADR 0775) — a pilot dossier's e-mail address. The core
/// maintains the resource-principal mapping from it, so a claim on this document becomes that person's.
/// <para>
/// Declarative rather than a facade call the module makes, deliberately. The mapping's whole purpose is that
/// a booking of this document counts as that person's commitment; a call that can be forgotten fails
/// SILENTLY and in the safe-looking direction — the person simply is not a claimant, their calendar is empty,
/// and nothing errors. Declaring the field instead means the mapping follows the value the module already
/// treats as the link, including when somebody edits it.
/// </para>
/// <para>
/// The field must be one of this mask's <paramref name="Fields"/> and should hold an e-mail address that
/// resolves to a user in the tenant. A value resolving to nobody leaves the document representing nobody —
/// which is the honest answer for a dossier filed before its pilot has an account — and says so at Warning
/// rather than failing the write.
/// </para></param>

public sealed record ModuleMaskSeed(
    Guid MaskId,
    string Name,
    bool IsFolderMask,
    bool IsBookable,
    IReadOnlyList<ModuleFieldSeed> Fields,
    bool AdmitsOnlyDeclaredChildren = false,
    IReadOnlyList<Guid>? AdmittedChildren = null,
    IReadOnlyList<Guid>? AllowedParents = null,
    string? RepresentsPrincipalField = null)
{
    /// <inheritdoc cref="NameVocabularyRelDocumentation"/>
    public string? NameVocabularyRel { get; init; }
}

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

    /// <summary>The Maintenance collection a bookable resource holds (core ADR 0778) — when it is
    /// UNAVAILABLE, as against when it is spoken for. Admitted implicitly by
    /// <see cref="ModuleMaskSeed.IsBookable"/> exactly as the Schedule is, so a module's bookable mask gets
    /// both without declaring either.</summary>
    public static readonly Guid Maintenance = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E46");

    /// <summary>The Availability collection a bookable resource holds (core ADR 0780) — its OFFERED time,
    /// as against when it is spoken for (Schedule) or withdrawn (Maintenance). Admitted implicitly by
    /// <see cref="ModuleMaskSeed.IsBookable"/> like the other two.</summary>
    public static readonly Guid Availability = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E48");

    /// <summary>A booking itself — the <c>.ics</c> in a resource's Schedule that IS the claim (core ADR
    /// 0744). Published so a module can name it in an <c>ActionSubjectMasks</c> declaration (ABI 0.20): a
    /// vertical offers actions on bookings it recognises, while deciding per document so it does not speak
    /// for every booking in the product.</summary>
    public static readonly Guid Booking = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E43");
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

/// <summary>
/// Marker for the shared documentation of <see cref="ModuleMaskSeed.NameVocabularyRel"/>.
/// </summary>
/// <remarks>
/// <para>
/// AN INIT-ONLY PROPERTY, NOT A PRIMARY-CONSTRUCTOR PARAMETER, and that is a rule rather than a preference
/// (#1147). Adding a parameter to a positional record CHANGES ITS CONSTRUCTOR SIGNATURE, so every module
/// compiled against the previous ABI minor calls a constructor that no longer exists — it loads cleanly and
/// then throws MissingMethodException from a static initializer. That took the public kiosk down for 94
/// minutes, and it is what a customer would meet on upgrading the core with an older module build.
/// </para>
/// <para>
/// An init-only property is additive: old modules never mention it and keep compiling and RUNNING; new ones
/// set it with an object initialiser. Every future optional member of this ABI goes in the same way.
/// </para>
/// One of this module's own <see cref="IIndustryModule.RootLinks"/> rels, whose endpoint answers
/// <c>?q=&lt;what has been typed&gt;</c> with a <see cref="ModuleVocabularyResource"/> (ABI 0.21). Documents
/// wearing this mask are then NAMED with a type-ahead over that vocabulary instead of from memory.
/// <para>
/// For the case where the name IS the identifier. A weather folder is called <c>LSZH</c> and a wrong code
/// fetches nothing; the module has always held the 3,706-entry aerodrome list and served a search over it,
/// but the create dialog is a bare text box, so the two never met and a user typed four letters from memory.
/// Reported exactly that way: "there's no completion".
/// </para>
/// <para>
/// It names a REL rather than a path, for the reason ADR 0543 gives — routes stay module-private and a rel
/// is the compatibility surface — and the core resolves it per tenant, emitting nothing when the module is
/// inactive. A rel naming no root link is ignored with a Warning: a silently absent type-ahead is
/// indistinguishable from one that was never declared, and that is the failure mode this whole feature
/// exists to remove.
/// </para>
/// <para>
/// Completion only, never validation. The user may type anything; whether a name is real stays the
/// module's own business at the moment it acts on it — the weather fetch already validates the ICAO on open
/// and stages a note for an unknown one. A type-ahead that refused unknown values would be a gate wearing a
/// convenience's clothes.
/// </para>
/// </remarks>
internal static class NameVocabularyRelDocumentation
{
}
