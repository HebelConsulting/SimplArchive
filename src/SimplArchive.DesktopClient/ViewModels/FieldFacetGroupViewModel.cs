using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimplArchive.DesktopClient.ViewModels;

// A per-Select-index-field facet dimension (ADR "Search facet refinements") — the field name + its value
// buckets, each clickable to drill the search down (multi-select OR within the field). Toggle delegates back
// to the parent VM, which owns the selection sets + re-search.
public sealed partial class FieldFacetGroupViewModel : ObservableObject
{
    private readonly Func<string, FacetBucketViewModel?, Task> _toggle;

    public FieldFacetGroupViewModel(string name, IEnumerable<FacetBucketViewModel> buckets, Func<string, FacetBucketViewModel?, Task> toggle)
    {
        Name = name;
        foreach (var bucket in buckets)
        {
            Buckets.Add(bucket);
        }

        _toggle = toggle;
    }

    public string Name { get; }

    public ObservableCollection<FacetBucketViewModel> Buckets { get; } = [];

    [RelayCommand]
    private Task Toggle(FacetBucketViewModel? bucket) => _toggle(Name, bucket);
}
