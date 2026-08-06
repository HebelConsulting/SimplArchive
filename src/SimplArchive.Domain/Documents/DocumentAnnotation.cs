using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A sticky note / positional annotation placed *on a page* of a document version —
// see ADR "Document annotations (sticky notes)". Distinct from the per-document comment/feed thread
// (DocumentComment): a comment is a message in a conversation, an annotation is pinned to a spot on the
// rendered page. Anchored to a specific DocumentVersion + page + a normalized (0..1, top-left origin)
// position, the same coordinate convention as the search hit-overlay word boxes.
//
// Editable (unlike the append-only comment thread), so it is IConcurrencyTracked — PUT/DELETE carry
// ETag/If-Match. Author is a User or a ServiceAccount, exactly one, the same pattern as DocumentComment.
public class DocumentAnnotation : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The document the note belongs to (denormalized for the "has notes" indicator + document-scoped
    // queries; the delete cascade flows through this FK, as with DocumentComment).
    public Guid DocumentId { get; set; }

    // The specific version the note is pinned to — a new version starts with a clean page.
    public Guid DocumentVersionId { get; set; }

    // 0-based index of the rendered page the note sits on.
    public int PageIndex { get; set; }

    // The markup kind (ADR "Annotation markup: highlight + shapes"; extended in ADR 0525) — Note (a point), a shape
    // (Highlight / Rectangle / Strikethrough / Arrow / Stamp / TextBox, carrying Width/Height) or Freehand (a stroke
    // path in Points). Default Note keeps existing rows unchanged.
    public AnnotationKind Kind { get; set; }

    // Normalized position of the note's anchor within the page, 0..1, top-left origin. For a box shape this is
    // the top-left corner; for an arrow, the start point.
    public double PositionX { get; set; }

    public double PositionY { get; set; }

    // Normalized extent of a shape (null for a Note): box size for Highlight/Rectangle (≥ 0); the signed offset
    // to the end point for an Arrow. Each in [-1, 1].
    public double? Width { get; set; }

    public double? Height { get; set; }

    // The note text; empty for a shape (shapes are optional-label markup). Carries the caption for Stamp/TextBox.
    public required string Text { get; set; }

    // A Freehand stroke's path: normalized points as space-separated "x,y" pairs ("0.10,0.20 0.11,0.22 …"), each
    // coordinate in [0,1]. Null for every other kind (which use PositionX/Y + Width/Height instead). ADR 0525.
    public string? Points { get; set; }

    // A short colour token (a hex like "#FFEB3B") from a small palette validated in the controller.
    public required string Color { get; set; }

    // How the text is rendered — font, size and the four styles (ADR 0542). Null for a shape, and for any
    // text-bearing annotation that simply uses the client's defaults, which is every annotation created before
    // this existed. Owned, so it maps to columns on this table rather than a join.
    public AnnotationTextStyle? TextStyle { get; set; }

    // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set.
    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Moves on every edit (text / position / colour).
    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
