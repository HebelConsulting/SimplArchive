using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.Localization;

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

    /// <summary>The hint itself: how much of the folder is on screen, shown only while a filter is applied.</summary>
    /// <remarks>
    /// The COUNTS are the content, not the word "filtered". A list narrowed to nothing and a genuinely empty
    /// folder look identical, and "0 of 27" tells the two apart at a glance — which is the whole question the
    /// hint exists to answer. Bound to a text block under the filter row rather than at the pane's foot, so it
    /// sits beside the inputs that caused it and cannot scroll out of view (ADR 0550).
    /// </remarks>
    public string ContentsFilterActiveHint =>
        string.Format(Strings.Get("ContentsFilterActiveHint"), VisibleItems.Count, Items.Count);

    /// <summary>Wired once from the constructor: the projection follows every mutation of <see cref="MainWindowViewModel.Items"/>.</summary>
    private void WireContentsFilter() => Items.CollectionChanged += (_, _) => RebuildVisibleItems();

    /// <summary>
    /// Drops every column filter. Called wherever the list stops describing the same folder (#1275).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A filter is a question about the list you are STANDING IN, and it stops meaning anything the moment you
    /// leave — unlike a sort order, which is a preference about how you like to read a list and may travel. So
    /// navigating always clears; a same-folder RELOAD after an action does not, because you have not left.
    /// </para>
    /// <para>
    /// Until this existed the filters were never cleared at all — not on navigation, not on returning to the
    /// roots, not even on LOGOUT, so one user's filter kept narrowing the next user's list. The web client has
    /// always cleared them (<c>ContentsListPane.ResetHeaderSort</c>), which is what made this a parity defect
    /// rather than a shared oversight: only one side had a test for it.
    /// </para>
    /// <para>
    /// The symptom is silent, which is why it survived: a filtered list looks exactly like a short one. The
    /// hint that would have explained it (<see cref="ContentsFilterActive"/>) is documented as existing so
    /// "where did my rows go?" has an answer, and is not bound in any view — so the answer was never shown.
    /// </para>
    /// </remarks>
    private void ClearContentsFilter() =>
        ContentsFilterName = ContentsFilterType = ContentsFilterTags = ContentsFilterOwner = string.Empty;

    private void RebuildVisibleItems()
    {
        var visible = Items.Where(Matches).ToList();

        // Raised BEFORE the idempotence check, because whether a filter is APPLIED is a different fact from
        // whether it CHANGED THE ROWS. A term matching everything present ("invoice" in a folder of invoices)
        // leaves the projection identical, so returning early without this would leave the hint hidden while
        // the filter was plainly in force — the gated-on-A-raised-in-B mistake, where the property that governs
        // the view is never told that the thing it depends on moved.
        OnPropertyChanged(nameof(ContentsFilterActive));

        // Cheap idempotence: CollectionChanged fires once per Add during ApplyContentSort's in-place rebuild,
        // so this runs O(n) times per sort — fine at folder sizes, but skip the Clear/Add churn (and the
        // selection flicker it would cause) when the result is already what the list shows.
        if (visible.SequenceEqual(VisibleItems))
        {
            // The hint still has to be raised here: the ROWS did not move, but the filter text did, and the
            // hint's other half is `ContentsFilterActive`'s visibility.
            OnPropertyChanged(nameof(ContentsFilterActiveHint));
            return;
        }

        VisibleItems.Clear();
        foreach (var n in visible)
        {
            VisibleItems.Add(n);
        }

        // AFTER the projection is rebuilt, never before: the hint reads VisibleItems.Count, so raising it up
        // with ContentsFilterActive would publish the PREVIOUS count — a number that is wrong precisely when
        // it is being read, which is worse than no number at all.
        OnPropertyChanged(nameof(ContentsFilterActiveHint));
    }

    private bool Matches(NodeViewModel n) =>
        (ContentsFilterName.Length == 0 || n.DisplayName.Contains(ContentsFilterName, StringComparison.OrdinalIgnoreCase))
        && (ContentsFilterType.Length == 0 || n.DocumentType.Contains(ContentsFilterType, StringComparison.OrdinalIgnoreCase))
        && (ContentsFilterTags.Length == 0 || n.Tags.Any(t => t.Contains(ContentsFilterTags, StringComparison.OrdinalIgnoreCase)))
        && (ContentsFilterOwner.Length == 0 || n.CreatedBy.Contains(ContentsFilterOwner, StringComparison.OrdinalIgnoreCase));
}
