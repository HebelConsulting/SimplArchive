using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.Views;

// Collects the export filters for a repository/folder (ADR "Repository export"). ShowDialog<RepositoryExportOptions?>
// returns the chosen filters, or null if cancelled. The caller (code-behind) does the API call + file save.
public partial class ExportDialog : Window
{
    // Parameterless ctor so the Avalonia XAML runtime loader can reach this window (AVLN3001).
    public ExportDialog() : this("") { }

    public ExportDialog(string rootName)
    {
        InitializeComponent();
        IntroText.Text = $"Export “{rootName}” and everything under it to a .zip archive.";
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var createdBy = (CreatedByBox.Text ?? "").Trim();
        Close(new DocumentsClient.RepositoryExportOptions(
            ActiveOnly: ActiveVersionRadio.IsChecked == true,
            DocumentDateFrom: ToDateOnly(DocDateFrom.SelectedDate),
            DocumentDateTo: ToDateOnly(DocDateTo.SelectedDate),
            FiledFrom: DocOffset(FiledFrom.SelectedDate),
            FiledTo: DocOffset(FiledTo.SelectedDate),
            CreatedBy: createdBy.Length == 0 ? null : createdBy,
            IncludePermissions: IncludePermissionsBox.IsChecked == true));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private static DateOnly? ToDateOnly(DateTimeOffset? value) => value is { } v ? DateOnly.FromDateTime(v.Date) : null;

    private static DateTimeOffset? DocOffset(DateTimeOffset? value) => value is { } v ? new DateTimeOffset(v.Date, TimeSpan.Zero) : null;
}
