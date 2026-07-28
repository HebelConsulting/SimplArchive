using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// Upload-time duplicate warning (ADR "Duplicate document detection"). ShowDialog<DuplicatePromptResult?> returns
// the chosen action (reference the selected existing document / file a second copy anyway), or null when cancelled.
public partial class DuplicateUploadDialog : Window
{
    public DuplicateUploadDialog()
    {
        InitializeComponent();
    }

    public DuplicateUploadDialog(MainWindowViewModel.DuplicatePromptRequest request) : this()
    {
        MessageBlock.Text = $"'{request.FileName}' is identical to "
            + (request.Duplicates.Count == 1 ? "an existing document:" : $"{request.Duplicates.Count} existing documents:");
        DuplicatesList.ItemsSource = request.Duplicates;
        DuplicatesList.SelectedIndex = 0;
    }

    private void OnReference(object? sender, RoutedEventArgs e)
    {
        var target = DuplicatesList.SelectedItem as SimplArchiveApiClient.DuplicateInfo
                     ?? (DuplicatesList.ItemsSource as System.Collections.IEnumerable)?.Cast<SimplArchiveApiClient.DuplicateInfo>().FirstOrDefault();
        Close(target is null ? null : new MainWindowViewModel.DuplicatePromptResult("reference", target.Id));
    }

    private void OnFileAnyway(object? sender, RoutedEventArgs e) => Close(new MainWindowViewModel.DuplicatePromptResult("file", Guid.Empty));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
