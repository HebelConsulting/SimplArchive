using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Search;

// A user's saved search (ADR "Saved searches") — a named, reusable snapshot of a full search: the assembled
// query-params string (free text + repository scope + index-field/system filters + facet drill-downs). Private
// to the user who created it. Append/rename/remove only — not versioned/soft-deletable/IConcurrencyTracked.
public class SavedSearch : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The owning user — saved searches are private (a ServiceAccount has none).
    public Guid UserId { get; set; }

    public required string Name { get; set; }

    // The raw search query-params string the client assembled, e.g. "q=foo&system[documentType][eq]=Invoice".
    public required string QueryString { get; set; }

    // The visibility scope (ADR "Scoped saved-search sharing", superseding the all-tenant IsShared bool):
    // Private (owner only, the default), Everyone (every tenant user), or Specific (the SavedSearchShare grants).
    // Only the owner (UserId) can edit/share/delete it.
    public ShareScope ShareScope { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
