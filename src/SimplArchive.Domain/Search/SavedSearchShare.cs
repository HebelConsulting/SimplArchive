using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Search;

// A grant of a saved search to a specific principal (ADR "Scoped saved-search sharing") — used when the search's
// ShareScope is Specific. Exactly one of UserId / GroupId is set; a share to a group is visible to every user in
// that group's effective (flow-down) membership. Append/remove only — not versioned/soft-deletable/
// IConcurrencyTracked (like DocumentReference / DocumentSubscription).
public class SavedSearchShare : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SavedSearchId { get; set; }

    // Exactly one of these is set (CHECK-enforced) — the granted principal.
    public Guid? UserId { get; set; }

    public Guid? GroupId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
