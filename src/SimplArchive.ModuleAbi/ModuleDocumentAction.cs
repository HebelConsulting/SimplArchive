namespace SimplArchive.ModuleAbi;

/// <summary>
/// An action a module offers on ONE document, chosen from a list of options (ABI 0.20, core ADR 0786).
/// </summary>
/// <remarks>
/// <para>
/// The generic surface for "pick somebody, then act" — the shape substitution needs and the shape the
/// clients already render for machine proposals. A proposal, though, is declared on a machine over the
/// module's OWN mask and exists to fill an index field; this is for an action on a document the module does
/// not own, committing something that is not a field.
/// </para>
/// <para>
/// <b>Rendered from hypermedia alone.</b> A client fetches <paramref name="OptionsPath"/>, shows the
/// choices, and POSTs the chosen value to <paramref name="CommitPath"/> — it needs no knowledge of the
/// module, which is the whole point: the core clients must not learn a vertical's features (ADR 0737).
/// </para>
/// </remarks>
/// <param name="Rel">The relation name, module-prefixed so two modules cannot collide
/// (<c>flight-school:hand-over</c>).</param>
/// <param name="Label">What the button says, already localized by the module.</param>
/// <param name="OptionsPath">Absolute path returning the choices — each an id/label/detail triple.</param>
/// <param name="CommitPath">Absolute path the chosen value is POSTed to.</param>
/// <param name="ValueField">The JSON property name the chosen value is sent as.</param>
/// <param name="Prompt">What the picker asks, already localized — shown above the choices.</param>
public sealed record ModuleDocumentAction(
    string Rel,
    string Label,
    string OptionsPath,
    string CommitPath,
    string ValueField,
    string Prompt);

/// <summary>What a module is told when asked which actions it offers on a document (ABI 0.20).</summary>
/// <param name="DocumentId">The document in question.</param>
/// <param name="MaskId">The mask it wears.</param>
/// <param name="Archive">The module's ordinary consent-gated read surface.</param>
/// <param name="Services">The request's service provider.</param>
public sealed record ModuleDocumentActionContext(
    Guid DocumentId,
    Guid? MaskId,
    IModuleArchiveFacade Archive,
    IServiceProvider Services);
