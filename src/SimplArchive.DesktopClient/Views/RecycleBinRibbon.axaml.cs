using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Recycle bin's ribbon (#530 tranche 1). The two destructive handlers stay on the window — each needs a
/// parent for its I-AGREE modal, which only the window has (the InboxRibbon/CheckoutRibbon shape); restore and
/// refresh bind to the view-model directly and have no forwarder here at all.
/// </summary>
public partial class RecycleBinRibbon : UserControl
{
    public RecycleBinRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnPurgeSelected(object? sender, RoutedEventArgs e) => Window()?.OnRecycleBinPurgeSelected(sender, e);

    private void OnHardDeleteAll(object? sender, RoutedEventArgs e) => Window()?.OnRecycleBinHardDeleteAll(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
