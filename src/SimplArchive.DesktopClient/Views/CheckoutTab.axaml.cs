using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Check-out tab's body (#519 tranche 3), on the extracted <see cref="CheckoutTabViewModel"/>.</summary>
public partial class CheckoutTab : UserControl
{
    public CheckoutTab() => AvaloniaXamlLoader.Load(this);

    // Moved with the markup: the named ListBox is in THIS control's namescope now, so the window can no longer
    // reach it. Reads the tab's own DataContext rather than the window's.
    private void OnCheckoutSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is CheckoutTabViewModel vm && sender is ListBox list)
        {
            vm.SetSelection(list.SelectedItems?.OfType<CheckoutRowViewModel>().ToList() ?? []);
        }
    }
}
