using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// Picks one sensitivity label (ADR "Configurable sensitivity labels + upload defaults") to apply to every
// selected document. Populated from the tenant's picker items ("(None)" + active labels). ShowDialog<...> returns
// the chosen SensitivityPickerItem (its Id is null for None), or null on cancel.
public partial class BulkSensitivityDialog : Window
{
    public BulkSensitivityDialog() : this([])
    {
    }

    public BulkSensitivityDialog(IEnumerable<MainWindowViewModel.SensitivityPickerItem> items)
    {
        InitializeComponent();
        LabelBox.ItemsSource = items.ToList();
        LabelBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(MainWindowViewModel.SensitivityPickerItem.Name));
        LabelBox.SelectedIndex = 0;
    }

    private void OnApply(object? sender, RoutedEventArgs e) => Close(LabelBox.SelectedItem as MainWindowViewModel.SensitivityPickerItem);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
