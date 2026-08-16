using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Tenant tab's ribbon (#530, tranche 10). All three launchers open windows/dialogs that need the window
/// as their parent (the RecycleBinRibbon shape), so this control only forwards.
/// </summary>
public partial class TenantRibbon : UserControl
{
    public TenantRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnManageSensitivityLabels(object? sender, RoutedEventArgs e) => Window()?.OnManageSensitivityLabels(sender, e);

    private void OnNewRepository(object? sender, RoutedEventArgs e) => Window()?.OnNewRepository(sender, e);

    private void OnConvertScans(object? sender, RoutedEventArgs e) => Window()?.OnConvertExistingTiffs(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
