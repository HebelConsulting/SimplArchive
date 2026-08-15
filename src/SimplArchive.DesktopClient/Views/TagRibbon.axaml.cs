using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Tag catalog ribbon (#530, tranche 6). The dialog-opening actions stay on the window (the
/// RecycleBinRibbon shape), so this control only forwards; retire and refresh bind to the view-model
/// directly and have no forwarder at all.
/// </summary>
public partial class TagRibbon : UserControl
{
    public TagRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnNewTag(object? sender, RoutedEventArgs e) => Window()?.OnNewTag(sender, e);

    private void OnRenameTag(object? sender, RoutedEventArgs e) => Window()?.OnRenameTag(sender, e);

    private void OnRecolourTag(object? sender, RoutedEventArgs e) => Window()?.OnRecolourTag(sender, e);

    private void OnMergeTag(object? sender, RoutedEventArgs e) => Window()?.OnMergeTag(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
