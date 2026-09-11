using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The picker a module action opens (core ADR 0786) — choose one, or cancel.</summary>
public partial class ModuleActionPickerDialog : Window
{
    public ModuleActionPickerDialog() => AvaloniaXamlLoader.Load(this);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ModuleActionPickerViewModel vm)
        {
            vm.Result = vm.Selected?.Value;
        }

        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
