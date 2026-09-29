namespace SimplArchive.ModuleAbi;

/// <summary>
/// What a module is handed when a document it projects has changed (ADR 0848, ABI 1.1).
/// </summary>
/// <param name="DocumentId">The document that changed. Its committed state is readable through
/// <paramref name="Archive"/>; the change itself is deliberately not described, because a projection
/// re-derives rather than patches.</param>
/// <param name="MaskId">The declared mask it wears — so a module projecting several kinds can branch without
/// a lookup.</param>
/// <param name="TenantId">The tenant it belongs to. Passed explicitly rather than left to an ambient
/// accessor: this runs inside the core's save, which a background sweep may have entered with no request
/// scope at all.</param>
/// <param name="Removed">True when the document is going away as far as readers are concerned — it was
/// soft-deleted, or its mask changed to one the module does not project. The module should drop its row
/// rather than re-derive it.</param>
/// <param name="Archive">The module's facade, for READING the committed document. Writing through it from
/// this hook re-enters the core's save — see <see cref="IIndustryModule.DocumentProjected"/>.</param>
/// <param name="Services">The host's provider, for the module's own registrations (its read-model context
/// above all).</param>
public sealed record ProjectedDocumentContext(
    Guid DocumentId,
    Guid MaskId,
    Guid TenantId,
    bool Removed,
    IModuleArchiveFacade Archive,
    IServiceProvider Services);
