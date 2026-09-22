using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The preview surface (toolbar + pages/text). Reused docked and in the full-screen overlay — see PreviewPane.axaml.
//
// The code-behind exists for the things only the view can do: how wide the pane currently is — which is what
// fit-width and fit-page are measured against, and which changes every time the user drags a splitter — the
// Ctrl/⌘+wheel gesture, and rendering the TEXT preview's find highlights (#1063): styled runs are built, not
// bound, and the scroll-to-the-active-match is a scroll offset only the view owns.
public partial class PreviewPane : UserControl
{
    public PreviewPane()
    {
        AvaloniaXamlLoader.Load(this);

        var scroll = this.FindControl<ScrollViewer>("PagesScroll")!;

        // Viewport rather than Bounds: it already excludes the scrollbars, so at zoom 1 the page is drawn exactly
        // as wide as the space it has and no horizontal scrollbar appears.
        scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
            {
                Vm?.SetViewport(scroll.Viewport);
            }
        };

        // The same PreviewPane instance is re-pointed at another view model (a tab switch), which has never been
        // measured — so hand it the size we already know.
        DataContextChanged += (_, _) =>
        {
            Vm?.SetViewport(scroll.Viewport);
            HookTextFind();
            ApplyWrap();
            RenderText();
        };
        HookTextFind();

        // The line-number gutter (#1317) redraws on every scroll tick and on any text re-layout (a splitter
        // drag changes the wrap width, which moves every wrapped line). It draws only the visible slice, so
        // the redraw is ~a viewport's worth of TextBlocks, not one per line of a long log.
        if (this.FindControl<ScrollViewer>("TextScroll") is { } textScroll)
        {
            textScroll.ScrollChanged += (_, _) => RenderGutter();
        }

        if (this.FindControl<SelectableTextBlock>("TextPreview") is { } textBlock)
        {
            textBlock.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty)
                {
                    RenderGutter();
                }
            };
        }

        // Tunnelling, so zoom wins over the ScrollViewer's own wheel scrolling.
        scroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    private PreviewViewModel? Vm => DataContext as PreviewViewModel;

    // ---- Text-preview find rendering (#1063) -----------------------------------------------------------

    private PreviewViewModel? _hooked;

    private void HookTextFind()
    {
        if (ReferenceEquals(_hooked, Vm))
        {
            return;
        }

        if (_hooked is not null)
        {
            _hooked.PropertyChanged -= OnVmPropertyChanged;
        }

        _hooked = Vm;
        if (_hooked is not null)
        {
            _hooked.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.PreviewText) or nameof(PreviewViewModel.TextFindStamp))
        {
            RenderText();
        }

        if (e.PropertyName is nameof(PreviewViewModel.PreviewWrap) or nameof(PreviewViewModel.PreviewText))
        {
            ApplyWrap();
        }
    }

    // The wrap toggle's whole effect (#1317): wrapping on the block, and the horizontal scrollbar FOLLOWING
    // from it — wrap on means nothing overflows sideways, wrap off means it can and the scrollbar appears.
    // The re-scroll keeps the active find match in view across the re-layout, and the gutter redraws because
    // every line's visual position just moved.
    private void ApplyWrap()
    {
        if (this.FindControl<SelectableTextBlock>("TextPreview") is not { } block
            || this.FindControl<ScrollViewer>("TextScroll") is not { } scroll || Vm is not { } vm)
        {
            return;
        }

        block.TextWrapping = vm.PreviewWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        scroll.HorizontalScrollBarVisibility = vm.PreviewWrap
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Dispatcher.UIThread.Post(() =>
        {
            RenderGutter();
            ScrollToActiveTextMatch();
        }, DispatcherPriority.Loaded);
    }

    // ---- Line numbers (#1317) --------------------------------------------------------------------------
    //
    // Always on, never selectable — a .log is read to be COPIED out of, so the numbers live in their own
    // column, positioned from the text block's OWN TextLayout rather than from an assumed line height:
    // measured is what keeps them aligned when wrapping makes one logical line occupy several visual ones.
    // Only the visible slice is drawn; the whole gutter for a 100k-line log would be 100k TextBlocks.
    private void RenderGutter()
    {
        if (this.FindControl<Canvas>("TextGutter") is not { } gutter)
        {
            return;
        }

        gutter.Children.Clear();
        if (this.FindControl<SelectableTextBlock>("TextPreview") is not { } block
            || this.FindControl<ScrollViewer>("TextScroll") is not { } scroll
            || Vm?.PreviewText is not { } text)
        {
            return;
        }

        var starts = SimplArchive.Presentation.TextLines.Starts(text);
        gutter.Width = 14 + Math.Max(2, starts.Count.ToString().Length) * 7.3;

        var muted = this.FindResource("WbMuted") as IBrush ?? Brushes.Gray;
        var viewTop = scroll.Offset.Y;
        var viewBottom = viewTop + scroll.Viewport.Height;
        var layoutLines = block.TextLayout.TextLines;

        var k = 0;
        var y = 0d;
        for (var i = 0; i < layoutLines.Count && k < starts.Count; i++)
        {
            var line = layoutLines[i];
            while (k < starts.Count && line.FirstTextSourceIndex >= starts[k])
            {
                AddGutterNumber(gutter, k + 1, y, viewTop, viewBottom, block.Padding.Top, muted);
                k++;
            }

            y += line.Height;
        }

        // A text without a trailing newline leaves its last start un-walked when the layout's final line
        // begins before it; and a trailing newline's empty last line may not get its own layout line at all.
        for (; k < starts.Count; k++)
        {
            AddGutterNumber(gutter, k + 1, y, viewTop, viewBottom, block.Padding.Top, muted);
        }
    }

    private void AddGutterNumber(Canvas gutter, int number, double lineY, double viewTop, double viewBottom, double padTop, IBrush muted)
    {
        var canvasY = padTop + lineY - viewTop;
        if (canvasY < -20 || padTop + lineY > viewBottom + 20)
        {
            return;
        }

        var num = new TextBlock
        {
            Text = number.ToString(),
            FontFamily = new FontFamily("Menlo,Consolas,monospace"),
            FontSize = 12,
            Foreground = muted,
            Width = gutter.Width - 8,
            TextAlignment = TextAlignment.Right,
        };
        Canvas.SetTop(num, canvasY);
        Canvas.SetLeft(num, 0);
        gutter.Children.Add(num);
    }

    // The web pane's mark colors (PreviewPane.razor), so the two clients light the same hits the same way.
    private static readonly IBrush HitBrush = new SolidColorBrush(Color.Parse("#ffe58a"));
    private static readonly IBrush ActiveHitBrush = new SolidColorBrush(Color.Parse("#f6a821"));
    private static readonly IBrush HitForeground = Brushes.Black;

    private void RenderText()
    {
        if (this.FindControl<SelectableTextBlock>("TextPreview") is not { } block || Vm is not { } vm)
        {
            return;
        }

        if (vm.PreviewText is not { } text)
        {
            block.Text = null;
            Dispatcher.UIThread.Post(RenderGutter, DispatcherPriority.Loaded);
            return;
        }

        if (vm.FindCount == 0)
        {
            block.Inlines = null;
            block.Text = text;
            Dispatcher.UIThread.Post(RenderGutter, DispatcherPriority.Loaded);
            return;
        }

        var inlines = new InlineCollection();
        foreach (var (segment, isHit, isActive) in vm.TextFindSegments())
        {
            inlines.Add(isHit
                ? new Run(segment) { Background = isActive ? ActiveHitBrush : HitBrush, Foreground = HitForeground }
                : new Run(segment));
        }

        block.Text = null;
        block.Inlines = inlines;
        Dispatcher.UIThread.Post(() =>
        {
            RenderGutter();
            ScrollToActiveTextMatch();
        }, DispatcherPriority.Loaded);
    }

    // MEASURED from the block's own TextLayout, not proportional-by-line (#1317). The old arithmetic said so
    // itself — "monospace, no wrapping, so line-of-offset over total lines maps straight onto the extent" —
    // and wrapping is now the DEFAULT, under which a logical line occupies several visual lines and that
    // mapping lands the wrong place. Walking the layout's own lines is exact in both modes.
    private void ScrollToActiveTextMatch()
    {
        if (this.FindControl<ScrollViewer>("TextScroll") is not { } scroll
            || this.FindControl<SelectableTextBlock>("TextPreview") is not { } block
            || Vm is not { } vm || vm.PreviewText is null
            || vm.ActiveTextMatchOffset is not (>= 0 and var offset))
        {
            return;
        }

        var y = 0d;
        foreach (var line in block.TextLayout.TextLines)
        {
            if (offset < line.FirstTextSourceIndex + line.Length)
            {
                break;
            }

            y += line.Height;
        }

        var target = block.Padding.Top + y - scroll.Viewport.Height / 2;
        scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(target, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is not { } vm || !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            return;
        }

        vm.ZoomBy(e.Delta.Y >= 0 ? PreviewZoom.WheelStep : 1 / PreviewZoom.WheelStep);
        e.Handled = true;
    }
}
