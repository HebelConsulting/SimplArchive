using SimplArchive.Api.Hypermedia;

namespace SimplArchive.Api.Documents;

// One version, over the wire — extracted from DocumentVersionsController by responsibility (the 1000-line
// rule): the controller keeps the HTTP edge, this file keeps the shape both clients read.
public class DocumentVersionResource : HypermediaResource
{
    public Guid Id { get; set; }

    public int? VersionNumber { get; set; }

    public string ObjectKey { get; set; } = string.Empty;

    public string? Sha256Hash { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>The version's approval-workflow status (Draft/InReview/Approved/Rejected/Released), or
    /// null when none was ever started — what lets a client label its workflow affordance by state
    /// ("Start" vs "Manage" vs "View") without following the workflow rel first (review round, ADR 0557).</summary>
    public string? WorkflowStatus { get; set; }

    // True when the `preview` link points at a server-generated rendition rather than the original file
    // shown as-is — the client badges it so the user knows it isn't the original (ADR "Converted-preview
    // overlay badge").
    public bool PreviewConverted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // The creator's display name (User.DisplayName / ServiceAccount.Name) — a read-only system field.
    public string CreatedByName { get; set; } = string.Empty;

    // The issuing date ("yyyy-MM-dd") — a string on the wire (XmlSerializer doesn't support DateOnly).
    public string DocumentDate { get; set; } = string.Empty;

    // The document date's optional time-of-day in UTC ("HH:mm"), or null when the date carries no time
    // (ADR "Optional time on the document date"). A string on the wire (XmlSerializer doesn't support TimeOnly).
    public string? DocumentTime { get; set; }

    // The version's OCR-language override (Tesseract "+"-joined; null = inherit the tenant default) — the
    // system-field picker on a TIFF version (ADR "Per-tenant / per-version OCR languages").
    public string? OcrLanguages { get; set; }

    // The file extension (e.g. ".tif"), derived from the object key — a read-only system field now that
    // Document.Name no longer carries it (ADR "Extension off Document.Name, derived from the object key").
    public string FileExtension { get; set; } = string.Empty;

    // The optional per-version comment (ADR 0528) — the "why this revision" note, shown in the versions dialog.
    public string? Comment { get; set; }

    /// <summary>
    /// The scanned-PDF detector's persisted conclusion (#999): "ConvertibleScan" / "NotAScan" /
    /// "Unreadable", or null while not yet judged. What the clients' OCR verdict line renders — the fact
    /// the worker used to compute and throw away, which is why a never-OCR'd PDF was inexplicable from
    /// the UI (ADR 0626's principle, moved from the logs into the pane).
    /// </summary>
    public string? OcrVerdict { get; set; }

    /// <summary>True when this version carries a digital signature — OCR would break it, so the OCR
    /// affordances are absent for it (the emitter and the enforcer share this predicate).</summary>
    public bool IsSigned { get; set; }

    /// <summary>
    /// True when this tenant delivers content ENVELOPED to the reader's own key rather than as plaintext
    /// (the strict tier — ADR 0825/0828). A capability on the resource, not a rel (#1352).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because the missing rels cannot be told apart. On a strict tenant a version legitimately
    /// carries no <c>download</c> and no <c>preview</c> — the seam refuses to presign, because no URL can be an
    /// envelope — and that is byte-for-byte what a version whose RENDITION FAILED looks like. So a client that
    /// reasoned from absence alone would say "no preview available" about a document it is simply not allowed to
    /// render, which is the wrong explanation for a permanent condition.
    /// </para>
    /// <para>
    /// <b>The web client is refused by construction, not by omission</b>, which is why this is worth a field. A
    /// browser cannot use a smartcard to decrypt: WebCrypto has no PKCS#11 bridge, client-certificate TLS
    /// authenticates the connection rather than decrypting a CMS blob, and the legacy hooks are long gone. Since
    /// the tier's claim is that the key never leaves the card, the browser is out — so the honest answer is to
    /// say so and name the desktop client, never to fall back to a plaintext door (#1351: a fallback would mean
    /// an attacker who controls the API need only decline to envelope).
    /// </para>
    /// <para>
    /// Deliberately about the TENANT's delivery rather than about this reader's credential: it stays true when
    /// the rels are present too (a reader who does hold a certificate gets <c>.p7m</c> bytes, which a browser
    /// still cannot render), so one field answers both shapes.
    /// </para>
    /// </remarks>
    public bool ContentIsEnveloped { get; set; }
}
