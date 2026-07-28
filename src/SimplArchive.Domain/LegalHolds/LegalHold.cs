using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.LegalHolds;

// A named legal hold / litigation matter (ADR "Legal hold & retention enforcement"). Placed by a User with
// CanLegalHold; covers a set of documents (LegalHoldItem). A document is "frozen" while it — or any ancestor —
// is covered by an ACTIVE hold (ReleasedAt == null): it can't be deleted, moved, renamed, re-versioned, or have
// its metadata changed. Releasing a matter sets ReleasedAt; a document stays frozen if another active hold
// still covers it (the point of modelling holds as named matters rather than a per-document flag).
public class LegalHold : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Reason { get; set; }

    // The User who placed the hold — CanLegalHold is a User-only right (no ServiceAccount equivalent).
    public Guid PlacedByUserId { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    // Null = active; set = released at that instant (no longer freezes its items).
    public DateTimeOffset? ReleasedAt { get; set; }

    public Guid? ReleasedByUserId { get; set; }
}
