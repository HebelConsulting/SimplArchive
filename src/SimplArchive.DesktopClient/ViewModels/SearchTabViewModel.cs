using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Search tab's own state and behaviour (#517 tranche 2), lifted out of <c>MainWindowViewModel</c>.
/// </summary>
/// <remarks>
/// <para>
/// Same seam as <see cref="RecycleBinTabViewModel"/> and <see cref="CheckoutTabViewModel"/>: the shell hands
/// over the api client, a status reporter, and the two dialog callbacks this tab needs — it does not reach
/// back into the shell. Measured before the move rather than assumed: of 418 lines, the only coupling was
/// <c>_api</c>, <c>Status =</c>, and ONE cross-tab hop (<c>SelectedTab = 3</c>, the Tags-to-Search bridge),
/// which stays on the shell where it belongs.
/// </para>
/// <para>
/// The tab owns its own <see cref="Preview"/>, exactly as the Recycle bin tab does, so a preview shown here
/// never leaks onto another tab.
/// </para>
/// </remarks>
public partial class SearchTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;

    /// <summary>Where this tab's status line goes — the shell wires it to its own status bar.</summary>
    private readonly IShellContext _shell;

    /// <summary>This tab's OWN preview, never shared with another tab.</summary>
    public PreviewViewModel Preview { get; }

    public SearchTabViewModel(IShellContext shell)
    {
        _shell = shell;
        Preview = new PreviewViewModel(shell);
    }

    public void SetApi(SimplArchiveApiClient api) => _api = api;

    /// <summary>Opening a result is the SHELL's act — it switches to Repositories and uses the shell's own
    /// preview — so this tab asks for it rather than doing it (#517 tranche 2).</summary>
    public Func<SearchResultViewModel, Task>? OpenResultRequested { get; set; }

    /// <summary>Greys the Search toolbar's Go to (#530, tranche 8); raised by OnSelectedSearchResultChanged.</summary>
    public bool HasSelectedSearchResult => SelectedSearchResult is not null;

    /// <summary>What the shell runs when this tab is activated: metadata once, saved searches every time.</summary>
    public async Task ActivateAsync()
    {
        if (!_searchMetadataLoaded)
        {
            await LoadSearchMetadataAsync();
        }

        await LoadSavedSearchesAsync();
    }

    /// <summary>Runs a tag search — the Tags tab's entry point; the shell switches tab, this runs the query.</summary>
    public async Task RunForTagAsync(string tag)
    {
        SearchQuery = string.Empty;
        ClearFacetSelections();
        _facetTagSet.Add(tag);
        await SearchAsync();
    }

    private void Report(string message) => _shell.Report(message);

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;

    [ObservableProperty] private string _searchStatus = string.Empty;

    [ObservableProperty] private SearchResultViewModel? _selectedSearchResult;

    public sealed record SearchRepoOption(Guid? Id, string Name);

    [ObservableProperty] private bool _filtersExpanded;

    private bool _searchMetadataLoaded;

    public ObservableCollection<SearchRepoOption> SearchRepositories { get; } = [];

    public ObservableCollection<FieldFilterRowViewModel> FieldFilters { get; } = [];

    [ObservableProperty] private SearchRepoOption? _selectedSearchRepository;

    [ObservableProperty] private DateTimeOffset? _docDateFrom;

    [ObservableProperty] private DateTimeOffset? _docDateTo;

    [ObservableProperty] private DateTimeOffset? _createdFrom;

    [ObservableProperty] private DateTimeOffset? _createdTo;

    [ObservableProperty] private string _createdByFilter = string.Empty;

    private IReadOnlyList<string> _availableFieldNames = [];

    private IReadOnlyDictionary<string, int> _fieldTypes = new Dictionary<string, int>();

    public bool CanAddFieldFilter => _availableFieldNames.Count > 0;

    private async Task LoadSearchMetadataAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var fields = await _api.Search.GetSearchFieldsAsync();
            _availableFieldNames = fields.Select(f => f.Name).ToList();
            _fieldTypes = fields.ToDictionary(f => f.Name, f => f.DataType);
            OnPropertyChanged(nameof(CanAddFieldFilter));

            SearchRepositories.Clear();
            SearchRepositories.Add(new SearchRepoOption(null, "All repositories"));
            foreach (var repo in await _api.Documents.GetRepositoriesAsync())
            {
                SearchRepositories.Add(new SearchRepoOption(repo.Id, repo.Name));
            }

            SelectedSearchRepository = SearchRepositories[0];
            _searchMetadataLoaded = true;
        }
        catch (Exception)
        {
            // Non-fatal — free-text/system filters still work without the field picker.
        }
    }

    [RelayCommand]
    private void AddFieldFilter()
    {
        if (_availableFieldNames.Count > 0)
        {
            FieldFilters.Add(new FieldFilterRowViewModel(_availableFieldNames, _fieldTypes));
        }
    }

    [RelayCommand]
    private void RemoveFieldFilter(FieldFilterRowViewModel row) => FieldFilters.Remove(row);

    // Everything the user entered: text, refinement filters and facet drill-downs, then re-run.
    //
    // The old "Clear filters" reset the panel and left the FACETS applied, so results stayed narrowed by
    // drill-downs that were no longer visible anywhere in the form — there was no way back to an unfiltered
    // search short of restarting (#462). Re-running matters too: an emptied form over stale results reads as
    // "reset did nothing".
    [RelayCommand]
    private async Task ResetSearchCriteriaAsync()
    {
        SearchQuery = string.Empty;
        SelectedSearchRepository = SearchRepositories.Count > 0 ? SearchRepositories[0] : null;
        DocDateFrom = DocDateTo = CreatedFrom = CreatedTo = null;
        CreatedByFilter = string.Empty;
        FieldFilters.Clear();
        ClearFacetSelections();
        SelectedSearchResult = null;
        Preview.Reset("");
        await SearchAsync();
    }

    private static void AddDateParam(List<string> parameters, string field, string op, DateTimeOffset? value)
    {
        if (value is { } date)
        {
            parameters.Add($"system[{field}][{op}]={date:yyyy-MM-dd}");
        }
    }

    // Runs a search assembled from the free text + repository scope + system/index-field filters.
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_api is null)
        {
            return;
        }

        var parameters = new List<string>();

        var query = SearchQuery.Trim();
        if (query.Length > 0)
        {
            parameters.Add($"q={Uri.EscapeDataString(query)}");
        }

        if (SelectedSearchRepository?.Id is { } repositoryId)
        {
            parameters.Add($"repositoryId={repositoryId}");
        }

        AddDateParam(parameters, "documentDate", "gte", DocDateFrom);
        AddDateParam(parameters, "documentDate", "lte", DocDateTo);
        AddDateParam(parameters, "createdAt", "gte", CreatedFrom);
        AddDateParam(parameters, "createdAt", "lte", CreatedTo);
        if (!string.IsNullOrWhiteSpace(CreatedByFilter))
        {
            parameters.Add($"system[createdBy][contains]={Uri.EscapeDataString(CreatedByFilter.Trim())}");
        }

        foreach (var row in FieldFilters)
        {
            var value = row.WireValue;
            if (string.IsNullOrEmpty(row.FieldName) || string.IsNullOrEmpty(value) || row.SelectedOperator is null)
            {
                continue;
            }

            parameters.Add($"fields[{Uri.EscapeDataString(row.FieldName)}][{row.SelectedOperator.Value}]={Uri.EscapeDataString(value)}");
        }

        // Active facet drill-downs (ADR "Search facet refinements") — each dimension's set becomes an `in` filter
        // (OR within the dimension); the server keeps each dimension open (post-filter faceting).
        AddFacetParam(parameters, "system[documentType][in]", _facetTypeSet);
        AddFacetParam(parameters, "system[fileType][in]", _facetFileTypeSet);
        AddFacetParam(parameters, "system[createdBy][in]", _facetCreatedBySet);
        AddFacetParam(parameters, "system[documentYear][in]", _facetYearSet);
        AddFacetParam(parameters, "system[tag][in]", _facetTagSet);
        AddFacetParam(parameters, "system[sensitivityLabel][in]", _facetSensitivitySet);
        foreach (var (field, set) in _facetFieldSets)
        {
            AddFacetParam(parameters, $"fields[{Uri.EscapeDataString(field)}][in]", set);
        }

        if (parameters.Count == 0)
        {
            SearchResults.Clear();
            FacetTypes.Clear();
            FacetFileTypes.Clear();
            FacetCreatedBy.Clear();
            FacetYears.Clear();
            FacetTags.Clear();
            FacetSensitivity.Clear();
            FieldFacets.Clear();
            HasFacetTypes = HasFacetFileTypes = HasFacetCreatedBy = HasFacetYears = HasFacetTags = HasFacetSensitivity = false;
            SearchStatus = string.Empty;
            return;
        }

        LastSearchQueryString = string.Join("&", parameters); // for "Save search" (ADR "Saved searches")
        await ExecuteSearchAsync(LastSearchQueryString);
    }

    // Appends `key=v1,v2` (comma-joined, escaped) when the facet set is non-empty (ADR "Search facet refinements").
    private static void AddFacetParam(List<string> parameters, string key, HashSet<string> values)
    {
        if (values.Count > 0)
        {
            parameters.Add($"{key}={string.Join(",", values.Select(Uri.EscapeDataString))}");
        }
    }

    private void ClearFacetSelections()
    {
        _facetTypeSet.Clear();
        _facetFileTypeSet.Clear();
        _facetCreatedBySet.Clear();
        _facetYearSet.Clear();
        _facetTagSet.Clear();
        _facetSensitivitySet.Clear();
        _facetFieldSets.Clear();
    }

    // Runs a pre-assembled query-params string (shared by the refinement search + a restored saved search).
    private async Task ExecuteSearchAsync(string queryParams)
    {
        if (_api is null)
        {
            return;
        }

        SearchStatus = Strings.Get("StSearching");
        try
        {
            var page = await _api.Search.SearchWithFacetsAsync(queryParams);
            SearchResults.Clear();
            foreach (var result in page.Results)
            {
                SearchResults.Add(new SearchResultViewModel
                {
                    Id = result.Id,
                    Name = result.Name,
                    IsFolder = result.IsFolder,
                    ParentId = result.ParentId,
                    Path = result.Path,
                    Highlight = result.Highlight,
                    VersionsHref = result.VersionsHref,
                    Links = result.Links,
                    MaskIconToken = result.Icon,
                });
            }

            PopulateFacets(FacetTypes, page.Facets.DocumentTypes, _facetTypeSet);
            PopulateFacets(FacetFileTypes, page.Facets.FileTypes, _facetFileTypeSet);
            PopulateFacets(FacetCreatedBy, page.Facets.CreatedBy, _facetCreatedBySet);
            PopulateFacets(FacetYears, page.Facets.Years, _facetYearSet);
            PopulateFacets(FacetTags, page.Facets.Tags, _facetTagSet);
            PopulateFacets(FacetSensitivity, page.Facets.SensitivityLabels, _facetSensitivitySet);
            HasFacetTypes = FacetTypes.Count > 0;
            HasFacetFileTypes = FacetFileTypes.Count > 0;
            HasFacetCreatedBy = FacetCreatedBy.Count > 0;
            HasFacetYears = FacetYears.Count > 0;
            HasFacetTags = FacetTags.Count > 0;
            HasFacetSensitivity = FacetSensitivity.Count > 0;

            // Per-Select-field facets (ADR "Search facet refinements") — one group per field, multi-select OR.
            FieldFacets.Clear();
            foreach (var field in page.Facets.Fields)
            {
                var set = _facetFieldSets.TryGetValue(field.Name, out var s) ? s : [];
                var buckets = field.Buckets.Select(b => new FacetBucketViewModel(b.Value, b.Count, set.Contains(b.Value)));
                FieldFacets.Add(new FieldFacetGroupViewModel(field.Name, buckets, ToggleFieldFacet));
            }

            SearchStatus = SearchResults.Count == 0 ? "No matches." : $"{SearchResults.Count} result(s).";
        }
        catch (Exception e)
        {
            SearchStatus = string.Format(Strings.Get("StErrSearch"), e.Message);
        }
    }

    // ---- Saved searches (ADR "Saved searches") ------------------------------------------------------
    public ObservableCollection<SearchClient.SavedSearchInfo> SavedSearches { get; } = [];

    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSaveSearch))] private string _lastSearchQueryString = string.Empty;

    public bool CanSaveSearch => !string.IsNullOrEmpty(LastSearchQueryString);

    // The view provides the "name this search" prompt (a native dialog can't be built in the VM).
    public Func<Task<string?>>? SaveSearchNamePrompt { get; set; }

    public async Task LoadSavedSearchesAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            SavedSearches.Clear();
            foreach (var s in await _api.Search.GetSavedSearchesAsync())
            {
                SavedSearches.Add(s);
            }
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    [RelayCommand]
    private async Task SaveCurrentSearch()
    {
        if (_api is null || !CanSaveSearch || SaveSearchNamePrompt is null)
        {
            return;
        }

        if (await SaveSearchNamePrompt() is not { } name || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await _api.Search.SaveSearchAsync(name.Trim(), LastSearchQueryString);
            Report(Strings.Get("StSearchSaved"));
            await LoadSavedSearchesAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
    }

    [RelayCommand]
    private async Task RunSavedSearch(SearchClient.SavedSearchInfo? s)
    {
        if (s is null)
        {
            return;
        }

        LastSearchQueryString = s.QueryString;
        SearchQuery = ExtractQ(s.QueryString);
        ClearFacetSelections();
        await ExecuteSearchAsync(s.QueryString);
    }

    [RelayCommand]
    private async Task DeleteSavedSearch(SearchClient.SavedSearchInfo? s)
    {
        if (_api is null || s is null)
        {
            return;
        }

        try { await _api.Search.DeleteSavedSearchAsync(s); } catch (Exception) { }
        await LoadSavedSearchesAsync();
    }

    // Set to a dialog runner (code-behind) that shows the share dialog for the VM and returns true on Save.
    public Func<ShareSavedSearchViewModel, Task<bool>>? ShowShareSavedSearchDialog { get; set; }

    // Open the scope dialog for my own saved search (ADR "Scoped saved-search sharing") — loads the picker
    // targets + current grants, then owner-only PUTs the chosen scope + principals.
    [RelayCommand]
    private async Task ShareSavedSearch(SearchClient.SavedSearchInfo? s)
    {
        if (_api is null || s is null || !s.IsMine || ShowShareSavedSearchDialog is null)
        {
            return;
        }

        try
        {
            var targets = await _api.Search.GetShareTargetsAsync();
            var current = s.ShareScope == 2
                ? (await _api.Search.GetSavedSearchSharesAsync(s)).Select(g => $"{g.PrincipalType}:{g.PrincipalId}").ToHashSet()
                : [];
            var options = targets.Select(t => new ShareSavedSearchViewModel.PrincipalOption(
                t.Type, t.Id, t.Type == "group" ? $"{t.Name} (group)" : t.Name, current.Contains($"{t.Type}:{t.Id}")));

            var dialogVm = new ShareSavedSearchViewModel(s.Name, s.ShareScope, options);
            if (!await ShowShareSavedSearchDialog(dialogVm))
            {
                return;
            }

            await _api.Search.SetSavedSearchShareAsync(s, dialogVm.Scope, dialogVm.SelectedPrincipals);
            Report(dialogVm.Scope switch { 1 => $"Shared '{s.Name}' with everyone.", 2 => $"Shared '{s.Name}' with specific people.", _ => $"'{s.Name}' is now private." });
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrSharing"), e.Message));
        }

        await LoadSavedSearchesAsync();
    }

    private static string ExtractQ(string queryString)
    {
        foreach (var part in queryString.Split('&'))
        {
            if (part.StartsWith("q=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part[2..]);
            }
        }

        return "";
    }

    // ---- Search facets (ADR "Search facets" / multi-select "Search facet refinements") ---------------
    public ObservableCollection<FacetBucketViewModel> FacetTypes { get; } = [];

    public ObservableCollection<FacetBucketViewModel> FacetFileTypes { get; } = [];

    public ObservableCollection<FacetBucketViewModel> FacetCreatedBy { get; } = [];

    public ObservableCollection<FacetBucketViewModel> FacetYears { get; } = [];

    public ObservableCollection<FacetBucketViewModel> FacetTags { get; } = [];

    public ObservableCollection<FacetBucketViewModel> FacetSensitivity { get; } = [];

    public ObservableCollection<FieldFacetGroupViewModel> FieldFacets { get; } = [];

    // Multi-select facet selections (ADR "Search facet refinements") — a set of chosen values per dimension
    // (OR within the dimension); a per-field dictionary keys the Select index-field facets by name.
    private readonly HashSet<string> _facetTypeSet = [];

    private readonly HashSet<string> _facetFileTypeSet = [];

    private readonly HashSet<string> _facetCreatedBySet = [];

    private readonly HashSet<string> _facetYearSet = [];

    private readonly HashSet<string> _facetTagSet = [];

    private readonly HashSet<string> _facetSensitivitySet = [];

    private readonly Dictionary<string, HashSet<string>> _facetFieldSets = [];

    [ObservableProperty] private bool _hasFacetTypes;

    [ObservableProperty] private bool _hasFacetFileTypes;

    [ObservableProperty] private bool _hasFacetCreatedBy;

    [ObservableProperty] private bool _hasFacetYears;

    [ObservableProperty] private bool _hasFacetTags;

    [ObservableProperty] private bool _hasFacetSensitivity;

    private static void PopulateFacets(ObservableCollection<FacetBucketViewModel> target, IReadOnlyList<SearchClient.SearchFacetBucket> buckets, HashSet<string> selected)
    {
        target.Clear();
        foreach (var b in buckets)
        {
            target.Add(new FacetBucketViewModel(b.Value, b.Count, selected.Contains(b.Value)));
        }
    }

    private Task ToggleFacet(HashSet<string> set, FacetBucketViewModel? b)
    {
        if (b is null)
        {
            return Task.CompletedTask;
        }

        if (!set.Remove(b.Value))
        {
            set.Add(b.Value);
        }

        return SearchAsync();
    }

    [RelayCommand] private Task ToggleFacetType(FacetBucketViewModel? b) => ToggleFacet(_facetTypeSet, b);

    [RelayCommand] private Task ToggleFacetFileType(FacetBucketViewModel? b) => ToggleFacet(_facetFileTypeSet, b);

    [RelayCommand] private Task ToggleFacetCreatedBy(FacetBucketViewModel? b) => ToggleFacet(_facetCreatedBySet, b);

    [RelayCommand] private Task ToggleFacetYear(FacetBucketViewModel? b) => ToggleFacet(_facetYearSet, b);

    [RelayCommand] private Task ToggleFacetTag(FacetBucketViewModel? b) => ToggleFacet(_facetTagSet, b);

    [RelayCommand] private Task ToggleFacetSensitivity(FacetBucketViewModel? b) => ToggleFacet(_facetSensitivitySet, b);

    private Task ToggleFieldFacet(string field, FacetBucketViewModel? b)
    {
        if (!_facetFieldSets.TryGetValue(field, out var set))
        {
            set = _facetFieldSets[field] = [];
        }

        return ToggleFacet(set, b);
    }

    // Leaving the tab for the document itself — from the row's button or a double-click (#462).
    [RelayCommand]
    private async Task OpenSearchResult(SearchResultViewModel result)
    {
        if (OpenResultRequested is { } open)
        {
            await open(result);
        }
    }

    // Opens a search result: switch to the Repositories tab and navigate to it (a folder opens itself; a
    // document opens its home folder and selects it).
    // Selecting a result previews it in the Search tab's own pane (#462), with the search terms seeded so the
    // hit overlay marks WHY it matched — the whole reason a preview here is worth more than a file viewer.
    //
    // Generated by [ObservableProperty] on _selectedSearchResult; before #462 the selection had no observer at
    // all and existed only for the double-click handler to read.
    partial void OnSelectedSearchResultChanged(SearchResultViewModel? value) => Safe.Fire(async () =>
    {
        OnPropertyChanged(nameof(HasSelectedSearchResult)); // greys the toolbar's Go to (#530 tranche 8)
        if (_api is null || value is null || value.IsFolder || value.VersionsHref is not { } versionsHref)
        {
            // A folder has nothing to preview, and clearing beats leaving the previous document's page sitting
            // under a folder's name.
            Preview.Reset("");
            return;
        }

        Preview.Reset(Strings.Get("StLoading"));
        Preview.FindQuery = SearchQuery.Trim();
        try
        {
            // Follows the address the ROW advertised rather than turning the id back into one (ADR 0557).
            await Preview.RenderAsync(await _api.Versions.GetPreviewFromVersionsAsync(versionsHref));
        }
        catch (Exception)
        {
            // The preview is an extra, never the tab: an unreachable rendition still leaves a usable result
            // list, and the user can still open the document.
            Preview.Reset("");
        }
    });

    /// <summary>Populates this tab for the headless screenshot mode — the search half of the demo state.</summary>
    internal void PopulateDemoForScreenshot()
    {
        SearchQuery = "invoice";
        SearchResults.Add(new SearchResultViewModel { Id = Guid.Empty, Name = "Zeta Invoice.pdf", IsFolder = false, ParentId = Guid.Empty, Path = "Repositories / Demo Repository / Invoices", Highlight = "…total amount due of CHF 1'240 for <em>invoice</em> number 2026-03, payable within 30 days…" });
        SearchResults.Add(new SearchResultViewModel { Id = Guid.Empty, Name = "Invoices", IsFolder = true, ParentId = Guid.Empty, Path = "Repositories / Demo Repository" });
        SearchResults.Add(new SearchResultViewModel { Id = Guid.Empty, Name = "March invoice run.xlsx", IsFolder = false, ParentId = Guid.Empty, Path = "Repositories / Demo Repository / 2026", Highlight = "Keywords: <em>invoice</em>, finance, 2026" });
        SearchStatus = "3 result(s).";

        // Show the refinement panel (ADR "Search-refinement UI", phase 2) populated, for the screenshot.
        SearchRepositories.Add(new SearchRepoOption(null, "All repositories"));
        SearchRepositories.Add(new SearchRepoOption(Guid.NewGuid(), "Demo Repository"));
        SelectedSearchRepository = SearchRepositories[0];
        _availableFieldNames = ["Amount", "Keywords", "Status"];
        _fieldTypes = new Dictionary<string, int> { ["Amount"] = 1, ["Keywords"] = 0, ["Status"] = 4 };
        CreatedByFilter = "Demo Admin";
        FieldFilters.Add(new FieldFilterRowViewModel(_availableFieldNames, _fieldTypes) { FieldName = "Amount", Value = "100" });
        FiltersExpanded = true;
    }
}
