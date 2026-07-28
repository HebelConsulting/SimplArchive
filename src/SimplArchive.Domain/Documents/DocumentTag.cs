using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A free-form tag/label on a document (ADR "Document tags") — cross-cutting (mask-independent), searchable
// categorization, distinct from a mask's structured index fields. Normalized to trimmed lowercase on write so
// "Invoice"/"invoice" collapse. ITenantScoped; append/remove only (not versioned/soft-deletable).
public class DocumentTag : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    // The normalized (trimmed, lowercased) tag text.
    public required string Tag { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
