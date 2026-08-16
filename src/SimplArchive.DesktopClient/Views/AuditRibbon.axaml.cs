using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Audit ribbon (#530, tranche 9). Export's save-file dialog and purge's confirm need the window as
/// their parent (the RecycleBinRibbon shape), so those handlers stay on the window and this control only
/// forwards; the verify pair binds to the view-model directly and has no forwarder at all.
/// </summary>
public partial class AuditRibbon : UserControl
{
    public AuditRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnExport(object? sender, RoutedEventArgs e) => Window()?.OnAuditExport(sender, e);

    private void OnPurge(object? sender, RoutedEventArgs e) => Window()?.OnAuditPurge(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
