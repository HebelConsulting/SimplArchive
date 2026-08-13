using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The preview surface (toolbar + pages/text). Reused docked and in the full-screen overlay — see PreviewPane.axaml.
//
// The code-behind exists for the two things the zoom model (#480) can only learn from the view: how wide the pane
// currently is — which is what fit-width and fit-page are measured against, and which changes every time the user
// drags a splitter — and the Ctrl/⌘+wheel gesture.
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
        DataContextChanged += (_, _) => Vm?.SetViewport(scroll.Viewport);

        // Tunnelling, so zoom wins over the ScrollViewer's own wheel scrolling.
        scroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    private PreviewViewModel? Vm => DataContext as PreviewViewModel;

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
