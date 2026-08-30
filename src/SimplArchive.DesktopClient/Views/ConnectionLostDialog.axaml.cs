using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// The "lost connection" modal (ADR "Desktop crash guard"). ShowDialog<string?> returns "reconnect" or
// "sign-out". The technical-details expander is shown only to tenant admins.
//
// The second button used to say "Close" and quit the app. It now signs out and returns to the logon window,
// and it is LABELLED that way: a control whose text promises one outcome and delivers another is the thing
// CLAUDE.md's state-transition principle exists to prevent, and "Close" beside a button that reopens a
// sign-in window was exactly that.
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

    private void OnSignOut(object? sender, RoutedEventArgs e) => Close("sign-out");
}
