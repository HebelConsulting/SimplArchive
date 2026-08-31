using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Recycle bin tab's body (#519 tranche 2), on the extracted <see cref="RecycleBinTabViewModel"/>.</summary>
public partial class RecycleBinTab : UserControl
{
    public RecycleBinTab() => AvaloniaXamlLoader.Load(this);

    // Moved with the markup it belongs to: the named ListBox is in THIS control's namescope now, so the
    // window can no longer reach it. Reads the tab's own DataContext rather than the window's, which is the
    // whole point of the extraction.
    private void OnRecycleBinSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is RecycleBinTabViewModel vm && sender is ListBox list)
        {
            vm.SetSelection(list.SelectedItems?.OfType<RecycleBinRowViewModel>().ToList() ?? []);
        }
    }
}
