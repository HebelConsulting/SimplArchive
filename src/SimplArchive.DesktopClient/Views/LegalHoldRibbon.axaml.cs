using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Legal holds ribbon (#530, tranche 5). Both actions open dialogs that need the window as their parent
/// (the RecycleBinRibbon shape), so the handlers stay on the window and this control only forwards; refresh
/// binds to the view-model directly and has no forwarder at all.
/// </summary>
public partial class LegalHoldRibbon : UserControl
{
    public LegalHoldRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnNewHold(object? sender, RoutedEventArgs e) => Window()?.OnNewLegalHold(sender, e);

    private void OnRelease(object? sender, RoutedEventArgs e) => Window()?.OnReleaseLegalHold(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
