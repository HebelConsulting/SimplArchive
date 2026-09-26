using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// The PIN that unlocks the card, asked ONCE per session (#1353 decision 3, ADR 0832).
//
// What the caller gets back is the string the user typed, and it is used exactly once — to open a PKCS#11
// login session — and then dropped. Nothing here stores it, and CardSession deliberately holds the SESSION
// rather than the secret, so there is no retained value for anything later to read.
public partial class CardPinDialog : Window
{
    /// <summary>What the user typed, or null if they cancelled.</summary>
    public string? Pin { get; private set; }

    public CardPinDialog()
    {
        InitializeComponent();
        Opened += (_, _) => PinBox.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter commits, because a PIN is typed and confirmed in one motion — reaching for the mouse after
        // four digits is exactly the friction that makes people choose a shorter PIN.
        if (e.Key == Key.Enter)
        {
            OnOk(sender, e);
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Pin = PinBox.Text;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Pin = null;
        Close();
    }
}
