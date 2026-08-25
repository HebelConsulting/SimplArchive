using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The tenant's mail domains (#667, ADR 0692) — the desktop twin of the web dialog (ADR 0511).
public partial class MailDomainsDialog : Window
{
    // The parameterless one exists for the XAML runtime loader, which is what AVLN3001 warns about when it is
    // missing — the same shape every sibling dialog here has. It was absent when this dialog was added, so the
    // build carried a warning; leaving one is what CLAUDE.md forbids, even a non-escalating one.
    public MailDomainsDialog() => InitializeComponent();

    public MailDomainsDialog(MailDomainsViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
