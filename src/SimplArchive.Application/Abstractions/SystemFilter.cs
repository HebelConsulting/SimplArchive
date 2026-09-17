namespace SimplArchive.Application.Abstractions;

// A filter on an indexed system field (ADR "System-field search") — parsed from ?system[Field][op]=value.
// Field is the OpenSearch field name (createdAt/versionCreatedAt/documentDate/createdBy/versionCreatedBy).
// Kind is Date (eq/gt/gte/lt/lte range) or Text (a resolved creator name: eq/contains/in). Only the
// OpenSearch path honors these; the metadata fallback ignores them.
public sealed record SystemFilter(string Field, FieldFilterKind Kind, string Operator, IReadOnlyList<string> Values);

// The structured filters accompanying a search: typed mask index-field filters (ADR 0043) plus system-field
// filters (ADR "System-field search"). Bundled so ISearchService.SearchAsync stays a manageable signature.
public sealed record SearchFilters(
    IReadOnlyCollection<FieldFilter> Fields,
    IReadOnlyCollection<SystemFilter> System,
    // The Select-type index-field names to compute per-field facets over (ADR "Search facet refinements") —
    // configuration, not a filter, so it doesn't affect IsEmpty. The OpenSearch path emits one nested terms
    // aggregation per name; the metadata fallback ignores it.
    IReadOnlyCollection<string>? FacetFields = null,
    // The zone the CALLER's dates are expressed in (#1254), or null to take them as UTC. A documentDate
    // filter is a question about a CALENDAR DAY — "the documents I filed on the 16th" — and the stored value
    // is an instant, so the two only agree once somebody says whose day is meant. Null keeps the old
    // behaviour for callers that do not care (an export filter, a script).
    string? TimeZoneId = null)
{
    public static readonly SearchFilters None = new([], []);

    public bool IsEmpty => Fields.Count == 0 && System.Count == 0;
}
