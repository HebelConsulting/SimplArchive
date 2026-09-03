using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// An immutable content version of a Document — see ADR "Document/DocumentVersion data shape
// (entities-only slice)", ADR "Document storage model", and ADR "Document version upload/download
// endpoints (pragmatic slice)" for the now-real ObjectKey/upload flow.
public class DocumentVersion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    // Pending = uploaded to storage but not yet finalized (VersionNumber/Sha256Hash still null);
    // Confirmed = finalized. VersionNumber is deliberately not assigned until confirmation, so an
    // abandoned/never-finalized upload doesn't burn a version number — see ADR "DocumentVersionsController
    // resource-oriented redesign".
    public DocumentVersionStatus Status { get; set; } = DocumentVersionStatus.Pending;

    public int? VersionNumber { get; set; }

    public required string ObjectKey { get; set; }

    // How many pages this version has, or null when nobody has needed to know yet — it is filled in as a
    // by-product of drawing the first-page thumbnail (issue #476), not by a pass over every version.
    //
    // A property of the version rather than of the thing that asked for it: the same document shared twice
    // should not count its pages twice, and a page count is useful well beyond the one caller that populates
    // it today. Null therefore means "not determined", never "no pages".
    public int? PageCount { get; set; }

    public string? Sha256Hash { get; set; }

    // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set — a ServiceAccount-driven upload
    // (the only real caller identity today, see ADR "ServiceAccount request authentication foundation")
    // has no User row to point at. See ADR "Document version upload/download endpoints (pragmatic
    // slice)". Document.CreatedByUserId itself is unchanged — there's no "create a Document" endpoint yet
    // for that gap to matter to.
    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // The document's issuing date — the date printed on/associated with the document
    // itself (e.g. an invoice date), which can be well in the past and is independent of CreatedAt (when it
    // was filed). Lives on DocumentVersion, NOT NULL — a re-issued version can carry its own date, and only
    // real content (not folders, which have no versions) has one. Defaults to CreatedAt's date at version
    // creation; editable via PUT .../document-date. See ADR "System-field search (creator/dates + document
    // date)".
    public DateOnly DocumentDate { get; set; }

    // Optional per-version OCR-language override for the TIFF → searchable-PDF conversion (ADR "Per-tenant /
    // per-version OCR languages") — a Tesseract "+"-joined multi-select of OcrLanguages.Supported codes. Null
    // = inherit the tenant's DefaultOcrLanguages. Set later via a mask field.
    public string? OcrLanguages { get; set; }

    /// <summary>
    /// The scanned-PDF detector's persisted conclusion (#999): null = not yet judged. Written by the
    /// searchable-PDF worker for PDFs (where the detector already runs, off the request path) and at
    /// finalize for TIFFs (trivially convertible). What the clients' OCR verdict line renders, and the fact
    /// that used to be computed and thrown away.
    /// </summary>
    public OcrVerdict? OcrVerdict { get; set; }

    // The confirmed blob's size in bytes (ADR "Per-tenant storage quota"), stamped at finalize. Feeds the
    // tenant's maintained StorageUsedBytes counter (added at confirm, subtracted at purge). Null for a pending
    // version or one written before the feature landed.
    public long? SizeBytes { get; set; }

    // An optional per-version comment — the "why this revision" note (ADR 0528). Its author is the version's
    // own CreatedBy; shown in the versions dialog. Null when none was given.
    public string? Comment { get; set; }

    // Where Comment came from. A machine-generated one carries no text at all: its wording is a localized
    // template the clients render, so a German user doesn't read an English sentence somebody's code wrote
    // (ADR 0545). Default UserComment, so every existing row keeps meaning exactly what it meant.
    public VersionCommentKind CommentKind { get; set; }

    // Whether this version's content carries a digital signature (#491), examined once at finalize — where the
    // bytes are already being read to verify the content hash, so it costs nothing extra.
    //
    // NULLABLE on purpose, and the three states are genuinely different: true = signed, false = examined and
    // not signed, null = NEVER EXAMINED, which is every version that predates this. A non-null default would
    // assert something untrue about the whole back catalogue, and the clients would then show "not signed"
    // where they should show nothing at all.
    //
    // What it gates: a signed document is never straightened, split, sorted or joined, because a signature
    // covers a byte range and any rewrite voids it — silently, since the file still opens and still looks right.
    public bool? IsSigned { get; set; }
}

// Who wrote a DocumentVersion.Comment (ADR 0545) — a person, or the system.
public enum VersionCommentKind
{
    // Typed by whoever filed the version: free text, stored verbatim, never translated.
    UserComment = 0,

    // Written by the searchable-PDF conversion. Comment stays NULL; the clients render the localized sentence.
    SearchablePdfGenerated = 1,
}
