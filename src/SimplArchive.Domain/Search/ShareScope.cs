namespace SimplArchive.Domain.Search;

// The visibility of a saved search (ADR "Scoped saved-search sharing") — replaces the old all-tenant IsShared
// bool with a single scope: private to the owner, shared with specific users/groups (via SavedSearchShare), or
// shared with everyone in the tenant.
public enum ShareScope
{
    Private = 0,
    Everyone = 1,
    Specific = 2,
}
