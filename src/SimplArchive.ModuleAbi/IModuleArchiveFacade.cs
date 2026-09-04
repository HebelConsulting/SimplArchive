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

    /// <summary>The documents directly under a parent wearing a given module mask — a dossier's
    /// certificates, a fleet's aircraft. Paged the core way; order is CreatedAt then Id.</summary>
    Task<IReadOnlyList<ModuleDocument>> GetChildrenAsync(Guid parentDocumentId, Guid maskId, CancellationToken cancellationToken = default);

    /// <summary>Every document in the tenant wearing a given module mask — what a projection REBUILD
    /// (ADR 0738) enumerates its subjects from. Order is CreatedAt then Id.</summary>
    Task<IReadOnlyList<ModuleDocument>> GetByMaskAsync(Guid maskId, CancellationToken cancellationToken = default);

    /// <summary>Creates a document wearing a module mask, with initial field values. The core's invariants
    /// (sibling names, containment, required fields) apply exactly as they do to any other write.</summary>
    Task<Guid> CreateDocumentAsync(Guid parentDocumentId, Guid maskId, string name, IReadOnlyDictionary<string, string>? fields = null, CancellationToken cancellationToken = default);

    /// <summary>Writes index fields on an existing document (the hour-meter reading at return).</summary>
    Task SetFieldsAsync(Guid documentId, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default);

    /// <summary>Creates a reference to a document in another folder — the SAME row, the reader's own
    /// rights: how one flight-log entry lands in the aircraft's, the pilot's and the instructor's books
    /// without a copy to diverge (module ADR 0002).</summary>
    Task CreateReferenceAsync(Guid targetDocumentId, Guid intoFolderId, CancellationToken cancellationToken = default);
}

/// <summary>A document as the facade shows it: identity, mask, and its index fields by name.</summary>
public sealed record ModuleDocument(
    Guid Id,
    Guid? ParentId,
    string Name,
    Guid? MaskId,
    IReadOnlyDictionary<string, string> Fields);
