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
            RenderText();
        };
        HookTextFind();

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
            return;
        }

        if (vm.FindCount == 0)
        {
            block.Inlines = null;
            block.Text = text;
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
        Dispatcher.UIThread.Post(ScrollToActiveTextMatch, DispatcherPriority.Loaded);
    }

    // Proportional by line: monospace, no wrapping, so line-of-offset over total lines maps straight onto the
    // extent. Exact enough that the active match lands mid-viewport, which is all a find needs.
    private void ScrollToActiveTextMatch()
    {
        if (this.FindControl<ScrollViewer>("TextScroll") is not { } scroll || Vm is not { } vm
            || vm.PreviewText is not { } text || vm.ActiveTextMatchOffset is not (>= 0 and var offset))
        {
            return;
        }

        var line = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        var totalLines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                totalLines++;
            }
        }

        var y = scroll.Extent.Height * line / totalLines - scroll.Viewport.Height / 2;
        scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
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
