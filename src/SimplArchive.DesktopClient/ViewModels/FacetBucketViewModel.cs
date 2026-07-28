namespace SimplArchive.DesktopClient.ViewModels;

// A search-facet bucket row (ADR "Search facets") — a value + its count, clickable to drill the search down.
// IsSelected marks the currently-applied facet in that group.
public sealed class FacetBucketViewModel(string value, long count, bool isSelected)
{
    public string Value { get; } = value;
    public long Count { get; } = count;
    public bool IsSelected { get; } = isSelected;
    public string Display => $"{Value} ({Count})";
}
