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
public sealed record ModuleMaskSeed(
    Guid MaskId,
    string Name,
    bool IsFolderMask,
    bool IsBookable,
    IReadOnlyList<ModuleFieldSeed> Fields);

/// <summary>One field of a module mask. The type vocabulary mirrors the core's field catalog.</summary>
/// <param name="Name">The field name, unique within the mask.</param>
/// <param name="DataType">One of the core's field data types by NAME ("Text", "Number", "Date", "DateTime",
/// "Boolean", "SingleSelect", "MultiSelect", "EmailAddress") — a string rather than a shared enum so the
/// ABI does not pin the core's enum ordinals into every compiled module (appending a core type must never
/// re-type a module's stored fields).</param>
/// <param name="IsRequired">Refused at activation when true and the mask is already worn — the same
/// protection the core's well-known heal has (a required field arriving later would invalidate documents).</param>
/// <param name="IsList">A repeatable field (the counters list on an Aircraft, ADR module-0004).</param>
public sealed record ModuleFieldSeed(
    string Name,
    string DataType,
    bool IsRequired = false,
    bool IsList = false);
