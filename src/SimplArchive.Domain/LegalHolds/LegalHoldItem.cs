using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.LegalHolds;

// A document covered by a legal hold (ADR "Legal hold & retention enforcement") — the join between LegalHold
// and Document. Many documents per hold; a document may be in several holds. Append/remove only.
public class LegalHoldItem : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid LegalHoldId { get; set; }

    public Guid DocumentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
