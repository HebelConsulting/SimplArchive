using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The tenant's mail domains (#667, ADR 0692) — the desktop twin of the web dialog (ADR 0511).
public partial class MailDomainsDialog : Window
{
    public MailDomainsDialog(MailDomainsViewModel model)
    {
        DataContext = model;
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
