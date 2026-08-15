using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// Pick the merge target for a catalog tag (#530, tranche 6). ShowDialog<TagCatalogRow?> returns the chosen
// target, or null if cancelled; the caller runs the view-model's merge command.
public partial class MergeTagDialog : Window
{
    // Parameterless ctor so the Avalonia XAML runtime loader can reach this window (AVLN3001).
    public MergeTagDialog() : this("?", []) { }

    public MergeTagDialog(string sourceName, IReadOnlyList<TagCatalogRow> candidates)
    {
        InitializeComponent();
        SourceText.Text = $"{Strings.Get("TagsMergeInto")} — {sourceName}";
        TargetBox.ItemsSource = candidates;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (TargetBox.SelectedItem is TagCatalogRow target)
        {
            Close(target);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
