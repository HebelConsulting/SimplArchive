using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>
/// Everything the Search tab has on screen — the query, the refinement filters, the facet drill-downs, and the
/// results themselves — held outside the component that renders them.
/// </summary>
/// <remarks>
/// <para>
/// The workbench renders one tab at a time, so <c>SearchTab</c> is created when its tab is shown and disposed
/// when another is. Search cannot afford that: opening a hit <em>navigates to the Repositories tab</em>, which
/// is the tab's single most common ending, so results held in the component would be gone precisely when the
/// user came back for the next hit. The desktop client already answers this — its results live on
/// <c>MainWindowViewModel</c>, which outlives every tab switch — and ADR 0511 makes that behaviour canonical.
/// </para>
/// <para>
/// So the state lives in DI and the component is a view over it. Registered scoped, which in a WebAssembly host
/// is the app's lifetime; that is deliberate, and it is also why <see cref="MetadataLoaded"/> lives here — the
/// field and repository pickers are a paged walk that should be paid once per session, not once per visit
/// (ADR 0557).
/// </para>
/// </remarks>
public sealed class SearchState
{
    /// <summary>The free-text term in the search box.</summary>
    public string Query { get; set; } = "";

    /// <summary>The current result set, flattened across every page of the last search.</summary>
    public List<SearchHit> Results { get; set; } = [];

    /// <summary>The drill-down dimensions the server returned for <see cref="Results"/>.</summary>
    public FacetsDto? Facets { get; set; }

    /// <summary>The status line under the search bar ("12 result(s).", "No matches.", a failure).</summary>
    public string Status { get; set; } = "";

    /// <summary>
    /// The query string the last search actually ran, which is what "Save search" persists — assembled from the
    /// whole UI, so it is not reconstructible from <see cref="Query"/> alone.
    /// </summary>
    public string LastQueryString { get; set; } = "";

    /// <summary>Whether the refinement panel is expanded.</summary>
    public bool FiltersExpanded { get; set; }

    /// <summary>The chosen repository scope; <c>null</c> means all repositories.</summary>
    public Guid? RepositoryId { get; set; }

    public DateTime? DocDateFrom { get; set; }

    public DateTime? DocDateTo { get; set; }

    public DateTime? CreatedFrom { get; set; }

    public DateTime? CreatedTo { get; set; }

    public string CreatedBy { get; set; } = "";

    /// <summary>The index-field filter rows the user has added.</summary>
    public List<FieldFilterRow> FieldFilters { get; set; } = [];

    /// <summary>The index fields a filter row may be built on (loaded once — see <see cref="MetadataLoaded"/>).</summary>
    public List<SearchFieldItem> AvailableFields { get; set; } = [];

    /// <summary>The repositories the scope picker offers (loaded once — see <see cref="MetadataLoaded"/>).</summary>
    public List<RepositorySummary> Repositories { get; set; } = [];

    /// <summary>Set once the field + repository pickers have been fetched, so a revisit does not re-walk them.</summary>
    public bool MetadataLoaded { get; set; }

    // Multi-select facet selections (ADR "Search facet refinements"): each dimension holds a set of chosen
    // values (OR within the dimension); a per-field dictionary keys the Select index-field facets by name.
    public HashSet<string> FacetTypes { get; } = [];

    public HashSet<string> FacetCreatedBy { get; } = [];

    public HashSet<string> FacetYears { get; } = [];

    public HashSet<string> FacetTags { get; } = [];

    public HashSet<string> FacetFileTypes { get; } = [];

    public HashSet<string> FacetSensitivity { get; } = [];

    public Dictionary<string, HashSet<string>> FacetFields { get; } = [];

    /// <summary>Drops every facet drill-down, leaving the query and the refinement filters alone.</summary>
    public void ClearFacetSelections()
    {
        FacetTypes.Clear();
        FacetCreatedBy.Clear();
        FacetYears.Clear();
        FacetTags.Clear();
        FacetFileTypes.Clear();
        FacetSensitivity.Clear();
        FacetFields.Clear();
    }

    /// <summary>
    /// The result being previewed, or null when nothing is selected (#462).
    /// </summary>
    /// <remarks>
    /// Here rather than in the component for the same reason as everything else on this class: following a hit
    /// switches to the Repositories tab, which disposes the tab, and coming back to a search whose selection had
    /// silently reset would lose the user's place on the path #462 makes the COMMON one (ADR 0558 rule 4).
    /// </remarks>
    public SearchHit? SelectedHit { get; set; }

    /// <summary>
    /// Everything the user has entered or narrowed: the query, the refinement filters, and every facet
    /// drill-down. This is what "Reset search criteria" means (#462).
    /// </summary>
    /// <remarks>
    /// It exists because the old "Clear filters" reset the refinement panel and left the FACETS applied, so the
    /// results stayed narrowed by drill-downs that were no longer visible anywhere in the form — a user could
    /// not get back to an unfiltered search except by reloading. Results and facets are deliberately NOT
    /// cleared: they describe the last search that ran, and blanking them would make the tab look broken until
    /// the next one completes.
    /// </remarks>
    public void ResetCriteria()
    {
        Query = "";
        RepositoryId = null;
        DocDateFrom = DocDateTo = CreatedFrom = CreatedTo = null;
        CreatedBy = "";
        FieldFilters = [];
        ClearFacetSelections();
    }
}

/// <summary>One index-field filter row in the refinement panel: which field, which operator, which value.</summary>
/// <remarks>
/// A mutable class rather than a record because the panel binds its inputs straight to these properties, and a
/// row is edited in place while it sits in <see cref="SearchState.FieldFilters"/>.
/// </remarks>
public sealed class FieldFilterRow
{
    public string FieldName { get; set; } = "";

    /// <summary>FieldDataType: Text=0, Number=1, Date=2, Boolean=3, SingleSelect=4, MultiSelect=5.</summary>
    public int DataType { get; set; }

    public string Operator { get; set; } = "";

    public string Value { get; set; } = "";

    /// <summary>Used instead of <see cref="Value"/> when <see cref="DataType"/> is Date.</summary>
    public DateTime? DateValue { get; set; }
}
