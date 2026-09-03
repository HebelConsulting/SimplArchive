using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Tenant tab's body (#530, tranche 10) — its own control on arrival, taking the whole per-group markup
/// out of MainWindow.axaml. The OCR picker dialog needs the window as its parent, so its Click forwards there;
/// everything else binds to the view-model directly.
/// </summary>
public partial class TenantSettingsPane : UserControl
{
    public TenantSettingsPane() => AvaloniaXamlLoader.Load(this);

    private void OnEditOcrLanguages(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnEditTenantOcrLanguages(sender, e);

    // The Activate/Renew dialog needs the window as its parent — same forwarding as the OCR picker above.
    private async void OnActivateModule(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window
            && (sender as Avalonia.Controls.Button)?.DataContext is ViewModels.ModuleRowViewModel row)
        {
            await TenantDialogs.OpenActivateModuleAsync(window, window.DataContext as ViewModels.MainWindowViewModel, row);
        }
    }
}
