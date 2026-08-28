using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.Views;

// A TextBlock whose text arrives as diff segments: emphasized ones get a background run, plain ones none.
// A control rather than a template because inline runs cannot be produced by an ItemsControl — inlines are
// not visuals — and a per-segment horizontal StackPanel would break text wrapping mid-line.
public sealed class DiffSegmentText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<DiffSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<DiffSegmentText, IReadOnlyList<DiffSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> EmphasisBrushProperty =
        AvaloniaProperty.Register<DiffSegmentText, IBrush?>(nameof(EmphasisBrush));

    public IReadOnlyList<DiffSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? EmphasisBrush
    {
        get => GetValue(EmphasisBrushProperty);
        set => SetValue(EmphasisBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty || change.Property == EmphasisBrushProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Inlines ??= [];
        Inlines.Clear();
        foreach (var segment in Segments ?? [])
        {
            var run = new Run(segment.Text);
            if (segment.Emphasized && EmphasisBrush is { } brush)
            {
                run.Background = brush;
            }

            Inlines.Add(run);
        }
    }
}
