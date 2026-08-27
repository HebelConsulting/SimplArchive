using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Repositories contents-list column model (ADRs "Desktop list-pane resizable columns" / 0705): the six
/// column widths, the flexible-Name rule that makes the table fill the pane, and the header drag arithmetic.
/// </summary>
/// <remarks>
/// Its own file because <c>MainWindowViewModel.cs</c> is on the 1000-line standing-debt list (issue #466) and
/// may only get smaller — so the work of #786 takes a home with it rather than paying for itself with a raised
/// ceiling. It is also a genuinely cohesive responsibility: everything here answers "how wide is each column",
/// and nothing else in the view-model asks.
/// </remarks>
public partial class MainWindowViewModel
{
    // Repositories contents-list column widths in pixels (ADR "Desktop list-pane resizable columns"): the
    // header and every row bind their cell widths to these, a horizontal scrollbar appears once the total
    // exceeds the pane, and the header's drag handles call ResizeColumn. Persisted in the layout file.
    // Name is the STORED width; the drawn width is ColNameWidth, computed from the pane (see below).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))][NotifyPropertyChangedFor(nameof(ColNameWidth))] private double _storedColNameWidth = DefaultColName;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))][NotifyPropertyChangedFor(nameof(ColNameWidth))] private double _colTypeWidth = DefaultColType;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))][NotifyPropertyChangedFor(nameof(ColNameWidth))] private double _colDateWidth = DefaultColDate;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))][NotifyPropertyChangedFor(nameof(ColNameWidth))] private double _colSizeWidth = DefaultColSize;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))][NotifyPropertyChangedFor(nameof(ColNameWidth))] private double _colTagsWidth = DefaultColTags;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))][NotifyPropertyChangedFor(nameof(ColNameWidth))] private double _colOwnerWidth = DefaultColOwner;

    private const double DefaultColName = 260, DefaultColType = 130, DefaultColDate = 96, DefaultColSize = 72, DefaultColTags = 160, DefaultColOwner = 140;
    private const double MinColumnWidth = 48;

    // The measured width of the list pane's scrollable viewport, pushed in by the view on every layout pass.
    // Zero until the first arrange, which is why the fallback below exists: at that moment the pane's width is
    // not a fact yet, and computing Name from it would draw a 48px stub for one frame.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ContentsTotalWidth))][NotifyPropertyChangedFor(nameof(ColNameWidth))] private double _contentsPaneWidth;

    private double OtherColumnsWidth => ColTypeWidth + ColDateWidth + ColSizeWidth + ColTagsWidth + ColOwnerWidth;

    /// <summary>
    /// The drawn width of the Name column — the FLEXIBLE one (#786), so the table fills the list pane exactly
    /// instead of being a fixed block inside it.
    /// </summary>
    /// <remarks>
    /// The slack goes to Name because it holds the longest content and is the column truncation actually hurts;
    /// Date, Size and Type cannot use extra width. Clamping at the minimum is what preserves the horizontal
    /// scrollbar: below that the total exceeds the pane and the region scrolls, so "fills the pane" and "never
    /// unreadable" do not conflict — one rule governs above the threshold and the other below it.
    /// </remarks>
    public double ColNameWidth => ContentsPaneWidth > 0
        ? Math.Max(MinNameWidth, ContentsPaneWidth - OtherColumnsWidth)
        : StoredColNameWidth;

    // Name's floor is its DEFAULT width, not the generic 48px minimum, and the difference is the whole feature.
    // The other five columns total 598px — more than a default list pane is wide — so a 48px floor made Name
    // collapse to a stub at every ordinary pane width and drew names as "In...", "sa...", "S...". That is worse
    // than the fixed-width behaviour it replaced.
    //
    // 48px is the right floor for a column a user deliberately dragged narrow; it is the wrong one for a
    // COMPUTED column, which nobody chose and which must stay useful on its own. With this floor the pane's
    // width decides which rule applies: wide enough and Name takes the slack, too narrow and Name holds its
    // default while the region overflows and scrolls — exactly the old behaviour, which was never the complaint.
    //
    // Found by LOOKING at a render, not by the arithmetic, which was correct and passing throughout.
    private const double MinNameWidth = DefaultColName;

    // The total pixel width of the columns — the width of the scrollable header+rows region. Equal to the pane
    // when there is room, and larger than it (so a horizontal scrollbar appears) only once Name has clamped.
    public double ContentsTotalWidth => ColNameWidth + OtherColumnsWidth;

    // Resize column `index` (0 Name … 5 Owner) by a pixel delta (from the header's drag handle), clamped to a
    // sensible minimum. Persisting is deferred to the drag's completion / window close.
    public void ResizeColumn(int index, double delta)
    {
        switch (index)
        {
            // Name has no independent width to set — it is the remainder. Dragging its edge moves width between
            // Name and its RIGHT NEIGHBOUR, which is what the pointer appears to be doing: Name's edge follows
            // the cursor, Type absorbs the difference, and every edge further right stays put. Doing it the
            // other way (pinning Name on first drag and flexing the last column instead) would make which column
            // is flexible depend on history, with nothing on screen to say which one it is.
            case 0: ColTypeWidth = Math.Max(MinColumnWidth, ColTypeWidth - delta); break;
            case 1: ColTypeWidth = Math.Max(MinColumnWidth, ColTypeWidth + delta); break;
            case 2: ColDateWidth = Math.Max(MinColumnWidth, ColDateWidth + delta); break;
            case 3: ColSizeWidth = Math.Max(MinColumnWidth, ColSizeWidth + delta); break;
            case 4: ColTagsWidth = Math.Max(MinColumnWidth, ColTagsWidth + delta); break;
            case 5: ColOwnerWidth = Math.Max(MinColumnWidth, ColOwnerWidth + delta); break;
        }
    }
}
