using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// The "Go to link…" prompt (#761): returns the pasted text, or null on cancel. Parsing and the error path
// live in the view-model (GoToDeepLinkAsync) — this window only collects the text.
public partial class GoToLinkDialog : Window
{
    public GoToLinkDialog()
    {
        InitializeComponent();
        Opened += (_, _) => LinkBox.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnGo(object? sender, RoutedEventArgs e) => Close(LinkBox.Text);
}
