using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// About box (ADR 0504): the constant vendor block (from the axaml) + the running client version, resolved
// from the same AssemblyInformationalVersion the auto-updater reads (ClientUpdate.RunningVersion).
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionBlock.Text = $"{Strings.Get("AboutVersion")} {ClientUpdate.RunningVersion}";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
