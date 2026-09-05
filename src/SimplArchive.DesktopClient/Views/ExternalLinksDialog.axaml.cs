using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// External links (ADR 0546, issue #385) — serves both the per-document dialog and the cross-document
// "My external links" view; the view-model decides which controls apply.
public partial class ExternalLinksDialog : Window
{
    // Parameterless ctor so the Avalonia XAML runtime loader can reach this window (AVLN3001).
    public ExternalLinksDialog() : this(null)
    {
    }

    public ExternalLinksDialog(ExternalLinksDialogViewModel? viewModel)
    {
        InitializeComponent();
        if (viewModel is not null)
        {
            DataContext = viewModel;
            Opened += async (_, _) => await viewModel.LoadAsync();

            // The clipboard is the view's to reach, so the view-model stays free of a toolkit type (ADR 0730):
            // the created URL is shown once, so being able to copy it is the point.
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
