namespace SimplArchive.Application.Abstractions;

// A single search hit — a document/folder matching the query in the current tenant (optionally one
// repository). ACL filtering + path building happen in the controller (per-hit CanSee). See ADR "Search /
// full-text indexing model" (0011). Highlight is a snippet fragment (content, else index-field value, else
// name) with the matched terms wrapped in <em>…</em> (ADR "Search result highlighting"); null when the
// implementation doesn't highlight (the Postgres fallback) or nothing textual matched (a filter-only search).
public sealed record SearchCandidate(Guid Id, string Name, bool IsFolder, Guid? ParentId, string? Highlight = null);

// A facet bucket: a value (document type / creator name / year / file type / index-field value) and how many
// hits carry it (ADR "Search facets"). Clicking one drills the search down by adding the corresponding filter.
public sealed record SearchFacetBucket(string Value, long Count);

// A per-index-field facet dimension (ADR "Search facet refinements") — the field name + its value buckets.
public sealed record SearchFieldFacet(string Name, IReadOnlyList<SearchFacetBucket> Buckets);

// Aggregated refinement counts over the current search + filters (ADR "Search facets"). Each dimension's
// counts reflect the OTHER active facet selections but not its own (post-filter faceting, ADR "Search facet
// refinements"), so multi-selecting within a dimension keeps its other values visible. OpenSearch-only — null
// on the Postgres metadata fallback.
public sealed record SearchFacets(
    IReadOnlyList<SearchFacetBucket> DocumentTypes,
    IReadOnlyList<SearchFacetBucket> CreatedBy,
    IReadOnlyList<SearchFacetBucket> Years,
    IReadOnlyList<SearchFacetBucket> Tags,
    IReadOnlyList<SearchFacetBucket> FileTypes,
    IReadOnlyList<SearchFacetBucket> SensitivityLabels,
    IReadOnlyList<SearchFieldFacet> Fields);

// A page of hits in ranked order, plus whether more raw hits exist beyond this window, plus the facet counts.
public sealed record SearchPage(IReadOnlyList<SearchCandidate> Hits, bool HasMore, SearchFacets? Facets = null);

// The search abstraction (ADR 0011's "abstracted behind an interface"). Two implementations: OpenSearch
// full-text when configured (relevance-ranked over names + index-field values + extracted document content),
// else a Postgres metadata-only fallback (ADR 0249). Results come back in ranked order via skip/take offset
// paging.
public interface ISearchService
{
    // The caller's SearchAccess (ADR "Indexed ACL in search") lets an implementation pre-filter by
    // visibility. When PreFiltersByAcl is true the returned hits are already ACL-filtered (accurate paging);
    // when false the controller still post-filters each hit by CanSee (ADR 0137). filters are the typed
    // index-field (ADR 0043) + system-field (ADR "System-field search") filters — combined with the free-text
    // query (AND); only the OpenSearch path honors them, the metadata fallback ignores them.
    Task<SearchPage> SearchAsync(
        string query, Guid? repositoryId, SearchAccess access, SearchFilters filters,
        int skip, int take, CancellationToken cancellationToken);

    bool PreFiltersByAcl { get; }
}
