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

    /// <summary>
    /// The value an administrator configured for one of this module's declared <see cref="ModuleSetting"/>s
    /// (ABI 0.12, core ADR 0772), for the CURRENT tenant. Null when nothing is configured — which is a normal
    /// state, not an error: the module degrades to whatever it does without that integration, and should say
    /// so where the absence is otherwise invisible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped to the CALLING module: the key is resolved against this module's own settings, so one module
    /// cannot read another's credential even knowing its key. Secrets are decrypted here — this is the only
    /// path that returns one in the clear, and the admin surface never does.
    /// </para>
    /// <para>
    /// Read it per use rather than caching it: an administrator who rotates a credential expects the next
    /// call to use the new one, and a cached secret outlives the reason it was fetched.
    /// </para>
    /// </remarks>
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Stages CONTENT bytes as an EPHEMERAL document under a module folder (ABI 0.6) — the "put a file in
    /// the archive" power a module lacked (create/read/set-fields/rename could not carry a byte payload).
    /// The bytes land in the tenant's own <c>fs/special/</c> area with an explicit <paramref name="expiresAt"/>
    /// the core's ephemeral-content sweep honours, so a transient flight-planning artefact — a DABS chart, a
    /// METAR — cannot outlive its validity. Pass <paramref name="replaceDocumentId"/> to replace an existing
    /// staged document IN PLACE (a new confirmed version on the same document, its name/fields/expiry
    /// refreshed) so a refresh never briefly shows two entries; null mints a new one. Returns the staged
    /// document's id. Subject to the same tenant/consent gate and invariants as every other write.
    /// Pass <paramref name="documentDate"/> (and optionally <paramref name="documentTime"/>, a UTC
    /// time-of-day) to DATE the artefact by its own content — a METAR by its observation time, a chart by its
    /// day (ABI 0.7, core ADR 0758) — rather than the default filing date; omitted, it defaults to today with
    /// no time.
    /// </summary>
    Task<Guid> StageContentAsync(
        Guid parentFolderId,
        Guid maskId,
        string name,
        byte[] content,
        string extension,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, string>? fields = null,
        Guid? replaceDocumentId = null,
        DateOnly? documentDate = null,
        TimeOnly? documentTime = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Files CONTENT bytes as a PERMANENT document under a module folder (ABI 0.6) — the companion to
    /// <see cref="StageContentAsync"/> for content that must persist rather than expire. This is how a module
    /// files REFERENCE DATA it ships and then reads back through <see cref="GetDocumentContentAsync"/>: the
    /// Europe ICAO aerodrome list the METAR/TAF autocomplete resolves against, filed as an ordinary archive
    /// document an administrator can inspect — and correct or extend — without a module redeploy. Writes to
    /// the tenant's normal archive keyspace (never <c>fs/special/</c>), with no expiry and no sweep. Pass
    /// <paramref name="replaceDocumentId"/> to replace an existing one IN PLACE (a new version) — how a
    /// heal-on-upgrade refreshes the shipped list against an admin's edits. Returns the document's id.
    /// </summary>
    Task<Guid> CreateContentDocumentAsync(
        Guid parentFolderId,
        Guid maskId,
        string name,
        byte[] content,
        string extension,
        IReadOnlyDictionary<string, string>? fields = null,
        Guid? replaceDocumentId = null,
        DateOnly? documentDate = null,
        TimeOnly? documentTime = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an EXISTING document's content with a new version, running the same finalize the upload path
    /// runs (ABI 0.15, core ADR 0781) — so a document whose bytes MEAN something to the core is re-read, and
    /// what they mean is updated with them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a module changes a booking: rewrite the <c>.ics</c> with a different <c>ATTENDEE</c> and
    /// the core's classifier turns that into the claim swap, vetted by
    /// <see cref="IIndustryModule.ReviewBooking"/>, audited and notified — one door, exactly as ADR 0744
    /// requires of every path that writes an <c>.ics</c>.
    /// </para>
    /// <para>
    /// <b>It is NOT <see cref="CreateContentDocumentAsync"/> with a replace id, and the difference is the
    /// whole reason it exists.</b> That path takes a deliberate shortcut — a confirmed version without the
    /// finalizer's tail — which is right for a METAR nobody interprets and silently WRONG for a booking: the
    /// bytes would name one instructor while the claim rows still named another, with nothing failing and no
    /// screen showing the disagreement.
    /// </para>
    /// <para>
    /// The document keeps its mask, its name and its place; only the bytes and their consequences change.
    /// Subject to every invariant an ordinary write is, so a refusal — a slot taken, a resource grounded, a
    /// vetting refusal from a module — surfaces here as the exception it would surface as anywhere else.
    /// </para>
    /// </remarks>
    /// <param name="documentId">The document to give a new version.</param>
    /// <param name="content">The new bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task ReplaceContentAsync(Guid documentId, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a resource's OFFERED time covers a slot (ABI 0.16, core ADR 0782) — the question a module asks
    /// when somebody's published availability is what stands in for their consent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Coverage, not overlap, and by a SINGLE window</b> — the semantic core ADR 0780 settled. A window
    /// ending at 12:00 does not consent to a flight running to 13:00 merely because the two touch, and two
    /// adjacent windows are not treated as one offer, because joining them would be an inference about
    /// somebody's intent. A resource offering continuous time publishes it as one window.
    /// </para>
    /// <para>
    /// Exposed rather than left to each module to compute from the window documents, because the rule above
    /// is a DEFINITION and a second copy of it is a second answer: the first module to test overlap instead
    /// of coverage would accept half an offer as consent for a whole booking, and nothing would say so.
    /// </para>
    /// <para>
    /// Answers for the calling tenant. A resource nobody has offered time for answers false, which is the
    /// honest answer and the safe one — absence of an offer is not consent.
    /// </para>
    /// </remarks>
    /// <param name="resourceDocumentId">The bookable resource whose offered time is in question.</param>
    /// <param name="startsAt">The slot's start.</param>
    /// <param name="endsAt">The slot's end, exclusive.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<bool> IsOfferedAsync(
        Guid resourceDocumentId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken = default);
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
