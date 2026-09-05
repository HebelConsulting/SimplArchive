namespace SimplArchive.ModuleAbi;

/// <summary>
/// What a module may do to the archive (ADR 0741) — the enumerated operation set, named as slice 1's first
/// ABI deliverable. Modules never see the core's entities or DbContext; every archive touch goes through
/// here, which is what makes a module's data reach auditable and the core's persistence refactorable.
/// </summary>
/// <remarks>
/// <para>
/// The v0.1 set is deliberately the SMALLEST that lets the flight-school slice-1 thread run: read a
/// document's identity and fields, create a document under a parent, write index fields, create a
/// reference (one entry, three logbooks — never copies), and resolve whether a principal may act. Widening
/// this interface is a conscious, versioned act — there is no back door (ADR 0741's consequence).
/// </para>
/// <para>
/// Every operation runs under the calling context's identity — the module's transition handlers run inside
/// the user's act and see what the user may see; fact providers and proposals run under the module's
/// service principal (ADR 0736). The host supplies the right context; the facade does not switch it.
/// </para>
/// </remarks>
public interface IModuleArchiveFacade
{
    /// <summary>A document's identity, mask, and index fields — the read every guard starts from.</summary>
    Task<ModuleDocument?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The current version's CONTENT bytes (ABI 0.3, #1024) — how a module parses a document it owns, the
    /// syllabus-JSON case above all (flight-school ADR 0003: the module parses its own document). Resolves
    /// the current version (honoring the CurrentVersionId pointer); null when the document has no confirmed
    /// version, or the module principal cannot see it (the same consent gate as the field reads). Loads the
    /// whole content — intended for the metadata-scale structured documents a module reads (a syllabus is a
    /// few KB), NOT for large binary content.
    /// </summary>
    Task<byte[]?> GetDocumentContentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>The documents directly under a parent wearing a given module mask — a dossier's
    /// certificates, a fleet's aircraft. Paged the core way; order is CreatedAt then Id.</summary>
    Task<IReadOnlyList<ModuleDocument>> GetChildrenAsync(Guid parentDocumentId, Guid maskId, CancellationToken cancellationToken = default);

    /// <summary>Every document in the tenant wearing a given module mask — what a projection REBUILD
    /// (ADR 0738) enumerates its subjects from. Order is CreatedAt then Id.</summary>
    Task<IReadOnlyList<ModuleDocument>> GetByMaskAsync(Guid maskId, CancellationToken cancellationToken = default);

    /// <summary>Creates a document wearing a module mask, with initial field values. The core's invariants
    /// (sibling names, containment, required fields) apply exactly as they do to any other write.</summary>
    Task<Guid> CreateDocumentAsync(Guid parentDocumentId, Guid maskId, string name, IReadOnlyDictionary<string, string>? fields = null, CancellationToken cancellationToken = default);

    /// <summary>Writes index fields on an existing document (the hour-meter reading at return).
    /// Single-valued fields only; a list field is written with <see cref="SetFieldListAsync"/>.</summary>
    Task SetFieldsAsync(Guid documentId, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces one LIST field's values, in order (ABI 0.2, #1014: the flight-log entry's named counter
    /// readings are three aligned list fields by decided design — module ADR 0004 — and v0.1 could not
    /// write a list at all). A replace-write like the core's own metadata PUT: what you pass is what the
    /// field holds afterwards; an empty list clears it.
    /// </summary>
    Task SetFieldListAsync(Guid documentId, string fieldName, IReadOnlyList<string> values, CancellationToken cancellationToken = default);

    /// <summary>Creates a reference to a document in another folder — the SAME row, the reader's own
    /// rights: how one flight-log entry lands in the aircraft's, the pilot's and the instructor's books
    /// without a copy to diverge (module ADR 0002).</summary>
    Task CreateReferenceAsync(Guid targetDocumentId, Guid intoFolderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a document (ABI 0.2, #1014): what lets a name be DERIVED from fields at the moment they
    /// are attested — the flight-log entry is born with a provisional name (times and airfields unknown
    /// until after the flight) and takes its official logbook line at Sign. The core's sibling-name
    /// invariant applies exactly as it does to any other rename.
    /// </summary>
    Task RenameDocumentAsync(Guid documentId, string name, CancellationToken cancellationToken = default);
}

/// <summary>A document as the facade shows it: identity, mask, and its index fields by name.</summary>
public sealed record ModuleDocument(
    Guid Id,
    Guid? ParentId,
    string Name,
    Guid? MaskId,
    IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>
    /// Every field's values in stored (ordinal) order (ABI 0.2, #1014) — the faithful shape.
    /// <see cref="Fields"/> keeps its joined single-string form so 0.1 modules read on unchanged; a list
    /// field is legible only here (a single-valued field appears in both, as a one-element list).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FieldLists { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty;
}
