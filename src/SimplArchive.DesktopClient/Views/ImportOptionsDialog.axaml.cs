using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Import options (ADRs "Idempotent re-import" / "ACL in export/import"). ShowDialog<ImportOptionsDialog.Result?>
// returns the update-existing + include-permissions flags, or null if cancelled. The caller (code-behind) already
// picked the file and does the import.
public partial class ImportOptionsDialog : Window
{
    // Parameterless ctor so the Avalonia XAML runtime loader can reach this window (AVLN3001).
    public ImportOptionsDialog() : this("") { }

    public ImportOptionsDialog(string fileName, string? targetName = null)
    {
        InitializeComponent();
        IntroText.Text = targetName is null
            ? $"Import “{fileName}” as a new repository."
            : $"Import “{fileName}” under “{targetName}”.";
        // Merge only makes sense when importing into an existing folder.
        MergeBox.IsVisible = targetName is not null;
        MergeBox.Content = $"Merge into “{targetName}” (reuse same-named folders)";
    }

    private void OnOk(object? sender, RoutedEventArgs e) =>
        Close(new Result(UpdateExistingBox.IsChecked == true, IncludePermissionsBox.IsChecked == true, MergeBox.IsChecked == true,
            MergeBox.IsChecked == true ? LeafConflictBox.SelectedIndex switch { 1 => "newVersion", 2 => "skip", _ => "rename" } : "rename"));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    public sealed record Result(bool UpdateExisting, bool IncludePermissions, bool Merge, string LeafConflict);
}
