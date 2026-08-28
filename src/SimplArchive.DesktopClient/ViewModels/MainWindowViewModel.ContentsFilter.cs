using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// The contents list's column filters (#48-queue item, the Tasks tab's pattern applied to the Repositories
// middle pane): a visible filter row under the column headers narrows the LOADED rows by name, type, tags and
// owner text (ADR 0550: the affordance is on screen, not behind a menu). Client-side over the loaded folder —
// which is the whole list, since a folder's children arrive unpaged.
//
// The ListBox binds VisibleItems, a projection of Items — NOT Items itself. Items stays the single source the
// rest of the shell reads and mutates (drag/drop, bulk actions, rename refresh, ApplyContentSort's in-place
// rebuild), and the projection follows it through one CollectionChanged subscription, so no mutation site
// needs to know the filter exists. The projection preserves Items' order, so sorting composes for free.
public partial class MainWindowViewModel
{
    /// <summary>What the contents list actually shows: <see cref="MainWindowViewModel.Items"/>, filtered.</summary>
    public ObservableCollection<NodeViewModel> VisibleItems { get; } = [];

    [ObservableProperty] private string _contentsFilterName = string.Empty;
    [ObservableProperty] private string _contentsFilterType = string.Empty;
    [ObservableProperty] private string _contentsFilterTags = string.Empty;
    [ObservableProperty] private string _contentsFilterOwner = string.Empty;

    partial void OnContentsFilterNameChanged(string value) => RebuildVisibleItems();
    partial void OnContentsFilterTypeChanged(string value) => RebuildVisibleItems();
    partial void OnContentsFilterTagsChanged(string value) => RebuildVisibleItems();
    partial void OnContentsFilterOwnerChanged(string value) => RebuildVisibleItems();

    /// <summary>True while any column filter narrows the list — the view shows a hint so "where did my rows go?" has an answer.</summary>
    public bool ContentsFilterActive =>
        ContentsFilterName.Length > 0 || ContentsFilterType.Length > 0
        || ContentsFilterTags.Length > 0 || ContentsFilterOwner.Length > 0;

    /// <summary>Wired once from the constructor: the projection follows every mutation of <see cref="MainWindowViewModel.Items"/>.</summary>
    private void WireContentsFilter() => Items.CollectionChanged += (_, _) => RebuildVisibleItems();

    private void RebuildVisibleItems()
    {
        var visible = Items.Where(Matches).ToList();

        // Cheap idempotence: CollectionChanged fires once per Add during ApplyContentSort's in-place rebuild,
        // so this runs O(n) times per sort — fine at folder sizes, but skip the Clear/Add churn (and the
        // selection flicker it would cause) when the result is already what the list shows.
        if (visible.SequenceEqual(VisibleItems))
        {
            return;
        }

        VisibleItems.Clear();
        foreach (var n in visible)
        {
            VisibleItems.Add(n);
        }
        OnPropertyChanged(nameof(ContentsFilterActive));
    }

    private bool Matches(NodeViewModel n) =>
        (ContentsFilterName.Length == 0 || n.DisplayName.Contains(ContentsFilterName, StringComparison.OrdinalIgnoreCase))
        && (ContentsFilterType.Length == 0 || n.DocumentType.Contains(ContentsFilterType, StringComparison.OrdinalIgnoreCase))
        && (ContentsFilterTags.Length == 0 || n.Tags.Any(t => t.Contains(ContentsFilterTags, StringComparison.OrdinalIgnoreCase)))
        && (ContentsFilterOwner.Length == 0 || n.CreatedBy.Contains(ContentsFilterOwner, StringComparison.OrdinalIgnoreCase));
}
