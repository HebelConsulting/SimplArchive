using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Confirmation for permanently emptying the whole recycle bin (ADR "Desktop recycle bin parity", mirroring the
// web ADR 0329): OK enables only once the user types the exact, case-sensitive phrase "I AGREE". ShowDialog<bool>
// returns true when confirmed.
public partial class HardDeleteAllDialog : Window
{
    private const string RequiredPhrase = "I AGREE";

    public HardDeleteAllDialog()
    {
        InitializeComponent();
        Opened += (_, _) => ConfirmBox.Focus();
        ConfirmBox.TextChanged += (_, _) => OkButton.IsEnabled = ConfirmBox.Text == RequiredPhrase;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(ConfirmBox.Text == RequiredPhrase);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
