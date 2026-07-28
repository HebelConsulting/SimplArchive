using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// The "lost connection" modal (ADR "Desktop crash guard"). ShowDialog<string?> returns "reconnect" or
// "close". The technical-details expander is shown only to tenant admins.
public partial class ConnectionLostDialog : Window
{
    public ConnectionLostDialog()
    {
        InitializeComponent();
    }

    public ConnectionLostDialog(bool showDetails, string details) : this()
    {
        DetailsExpander.IsVisible = showDetails;
        DetailsText.Text = details;
    }

    private void OnReconnect(object? sender, RoutedEventArgs e) => Close("reconnect");

    private void OnClose(object? sender, RoutedEventArgs e) => Close("close");
}
