using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

// One side-by-side row of a comparison, ready to render (ADR 0712). The ROWS — alignment, kinds, word
// emphasis — come from the shared SimplArchive.Presentation.TextDiff, so this class only translates them
// into brushes and strings; it decides nothing both clients must agree on.
public sealed class DiffRowViewModel
{
    private static readonly IBrush RemovedTint = new SolidColorBrush(Color.FromArgb(40, 244, 67, 54));
    private static readonly IBrush AddedTint = new SolidColorBrush(Color.FromArgb(40, 76, 175, 80));
    private static readonly IBrush RemovedEmphasis = new SolidColorBrush(Color.FromArgb(96, 244, 67, 54));
    private static readonly IBrush AddedEmphasis = new SolidColorBrush(Color.FromArgb(96, 76, 175, 80));
    private static readonly IBrush GapTint = new SolidColorBrush(Color.FromArgb(14, 128, 128, 128));

    private DiffRowViewModel(DiffRow row)
    {
        OldNumber = row.Old is { } o ? o.LineNumber.ToString() : string.Empty;
        NewNumber = row.New is { } n ? n.LineNumber.ToString() : string.Empty;
        OldSegments = row.Old?.Segments ?? [];
        NewSegments = row.New?.Segments ?? [];

        OldBackground = row.Kind switch
        {
            DiffRowKind.Changed or DiffRowKind.Removed => RemovedTint,
            DiffRowKind.Added => GapTint, // the gap an insertion leaves on the old side
            _ => Brushes.Transparent,
        };
        NewBackground = row.Kind switch
        {
            DiffRowKind.Changed or DiffRowKind.Added => AddedTint,
            DiffRowKind.Removed => GapTint,
            _ => Brushes.Transparent,
        };
    }

    public string OldNumber { get; }
    public string NewNumber { get; }
    public IReadOnlyList<DiffSegment> OldSegments { get; }
    public IReadOnlyList<DiffSegment> NewSegments { get; }
    public IBrush OldBackground { get; }
    public IBrush NewBackground { get; }

    // The old side emphasizes in the removed color, the new side in the added one.
    public IBrush OldEmphasisBrush => RemovedEmphasis;
    public IBrush NewEmphasisBrush => AddedEmphasis;

    public static List<DiffRowViewModel> Build(string fromText, string toText) =>
        [.. TextDiff.Compute(fromText, toText).Select(r => new DiffRowViewModel(r))];
}
