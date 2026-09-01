using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// One external link's details, read-only apart from renewal (ADR 0546).
public partial class ExternalLinkDetailDialog : Window
{
    // Parameterless ctor so the Avalonia XAML runtime loader can reach this window (AVLN3001).
    public ExternalLinkDetailDialog() : this(null)
    {
    }

    public ExternalLinkDetailDialog(ExternalLinkDetailDialogViewModel? viewModel)
    {
        InitializeComponent();
        if (viewModel is not null)
        {
            DataContext = viewModel;
            // The view-model closes the window itself once a renewal lands, so the list behind it reloads without
            // the reader having to dismiss a dialog whose numbers are already stale.
            viewModel.RequestClose = Close;

            // The clipboard is the view's to reach, so the view-model stays free of a toolkit type: revealing
            // a URL is only useful if it can be pasted somewhere.
            viewModel.CopyToClipboard = async text =>
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            };
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
