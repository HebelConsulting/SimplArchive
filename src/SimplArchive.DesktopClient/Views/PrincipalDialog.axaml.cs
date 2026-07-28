using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// New/Copy a user or group (ADR "Users & groups administration tab"). ShowDialog<PrincipalDialog.Result?>
// returns the entered fields, or null if cancelled/incomplete. For a group only the name is shown; for a
// user, email + display name. For Copy, the fields are pre-filled from the selected principal.
public partial class PrincipalDialog : Window
{
    public PrincipalDialog() : this(false, "", "")
    {
    }

    public PrincipalDialog(bool isGroup, string initialName, string initialEmail)
    {
        InitializeComponent();
        Title = isGroup ? "New group" : "New user";
        EmailPanel.IsVisible = !isGroup;
        NameLabel.Text = isGroup ? "Group name" : "Display name";
        NameBox.Text = initialName;
        EmailBox.Text = initialEmail;
        _isGroup = isGroup;
        Opened += (_, _) => (isGroup ? NameBox : EmailBox).Focus();
    }

    private readonly bool _isGroup;

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var email = EmailBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name) || (!_isGroup && string.IsNullOrEmpty(email)))
        {
            return;
        }

        Close(new Result(name, email));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    public sealed record Result(string Name, string Email);
}
