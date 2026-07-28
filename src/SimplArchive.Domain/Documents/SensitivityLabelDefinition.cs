using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A per-tenant, configurable data-classification label (ADR "Configurable sensitivity labels + upload defaults",
// superseding the fixed None/Public/Internal/Confidential/Restricted enum of ADR 0399). A Document points at one
// (or none) via Document.SensitivityLabelId; a MaskVersion may name one as its upload-time default. Rank orders
// the set by severity (and drives display order); Watermark decides whether this label triggers the ADR 0400
// preview watermark + "sensitive" treatment. Retiring keeps a label on existing documents but stops offering it.
public class SensitivityLabelDefinition : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Unique per tenant. Indexed into search as the document's sensitivity keyword.
    public required string Name { get; set; }

    // Severity + display order (higher = more sensitive). The seeded defaults use 1..4 (Public..Restricted).
    public int Rank { get; set; }

    // Badge colour, "#RRGGBB".
    public string? Color { get; set; }

    // When true, a document with this label is watermarked in the preview (ADR 0400) — the tenant decides
    // exactly which labels are sensitive, independent of Rank.
    public bool Watermark { get; set; }

    // Null = active; set = retired (kept on existing documents, not offered for new classification).
    public DateTimeOffset? RetiredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
