using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Create a legal hold / matter (ADR "Legal hold & retention enforcement"). ShowDialog<LegalHoldDialog.Result?>
// returns the matter name + optional reason, or null if cancelled. The caller (VM) does the API call.
public partial class LegalHoldDialog : Window
{
    // Parameterless ctor so the Avalonia XAML runtime loader can reach this window (AVLN3001).
    public LegalHoldDialog() : this(null) { }

    public LegalHoldDialog(string? suggestedName)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            NameBox.Text = $"Hold: {suggestedName}";
        }

        Opened += (_, _) => NameBox.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            return;
        }

        var reason = (ReasonBox.Text ?? "").Trim();
        Close(new Result(name, reason.Length == 0 ? null : reason));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    public sealed record Result(string Name, string? Reason);
}
